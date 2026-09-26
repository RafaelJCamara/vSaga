namespace VSaga.Persistence.Redis;

/// <summary>
/// The provider's Lua, each script a fixed reviewed string. Every key is computed in C# and passed as
/// <c>KEYS</c>; the exceptions are the claim and cancel scripts, which append a row id read from a sorted
/// set to a prefix passed in <c>ARGV</c> -- string concatenation, which cannot raise. No <c>cjson</c>:
/// <c>dataJson</c> is the user's serialised state and must round-trip byte-identically, so JSON stays
/// opaque bytes to Redis. No unbounded loops: every loop is over a batch the caller bounded.
/// </summary>
internal static class RedisLuaScripts
{
    /// <summary>
    /// The one committer. Mode <c>0</c> inserts a snapshot, <c>1</c> updates one under a version
    /// compare-and-set, <c>2</c> commits staged outbox rows alone (the conformance suite's unit-of-work
    /// hook). The write order is load-bearing and is the plan's, not the implementer's:
    /// <list type="number">
    /// <item>torn-write sentinel, first;</item>
    /// <item>existence / version check -- read-only, aborting with a sentinel code;</item>
    /// <item>business-key reserve (<c>SET NX</c>) and release;</item>
    /// <item>each staged outbox row hash -- invisible, since nothing indexes it yet;</item>
    /// <item>the snapshot hash;</item>
    /// <item>every summary index;</item>
    /// <item>the outbox pending index -- rows become publishable here, so a script that died earlier left garbage, never a phantom publish;</item>
    /// <item>sentinel removed, last.</item>
    /// </list>
    /// Returns <c>1</c>, or a negative sentinel: <c>-1</c> version mismatch, <c>-2</c> instance absent,
    /// <c>-3</c> instance already exists, <c>-4</c> business key taken. Only these map to domain
    /// exceptions; any Redis error is an infrastructure failure.
    /// </summary>
    public const string Persist = """
        local mode = ARGV[1]
        local member = ARGV[8]
        local fixedKeys = 16
        local rowCount = #KEYS - fixedKeys

        local fieldCount = tonumber(ARGV[15])
        local cursor = 16
        local snapshot = {}
        for i = 1, 2 * fieldCount do
          snapshot[i] = ARGV[cursor]
          cursor = cursor + 1
        end

        local rows = {}
        for r = 1, rowCount do
          local row = { key = KEYS[fixedKeys + r], score = ARGV[cursor], messageId = ARGV[cursor + 1], fields = {} }
          local pairs = tonumber(ARGV[cursor + 2])
          cursor = cursor + 3
          for i = 1, 2 * pairs do
            row.fields[i] = ARGV[cursor]
            cursor = cursor + 1
          end
          rows[r] = row
        end

        local function abort(code)
          redis.call('HDEL', KEYS[1], ARGV[3])
          return code
        end

        if mode ~= '2' then
          redis.call('HSET', KEYS[1], ARGV[3], ARGV[4])
          if mode == '0' then
            if redis.call('EXISTS', KEYS[2]) == 1 then return abort(-3) end
          else
            local version = redis.call('HGET', KEYS[2], 'version')
            if not version then return abort(-2) end
            if version ~= ARGV[2] then return abort(-1) end
          end
          if ARGV[5] == '1' then
            if not redis.call('SET', KEYS[4], ARGV[7], 'NX') then return abort(-4) end
          end
          if ARGV[6] == '1' and redis.call('GET', KEYS[3]) == ARGV[7] then
            redis.call('DEL', KEYS[3])
          end
        end

        for _, row in ipairs(rows) do
          redis.call('DEL', row.key)
          redis.call('HSET', row.key, unpack(row.fields))
          redis.call('HSET', row.key, 'id', redis.call('INCR', KEYS[16]))
        end

        if mode ~= '2' then
          redis.call('DEL', KEYS[2])
          redis.call('HSET', KEYS[2], unpack(snapshot))
          if ARGV[14] == '1' then
            error('vsaga: test hook aborting the persist script after the snapshot write')
          end
          if mode == '1' then
            if KEYS[6] ~= KEYS[7] then redis.call('ZREM', KEYS[6], member) end
            if KEYS[8] ~= KEYS[9] then redis.call('ZREM', KEYS[8], member) end
          end
          redis.call('ZADD', KEYS[5], ARGV[9], member)
          redis.call('ZADD', KEYS[7], ARGV[9], member)
          redis.call('ZADD', KEYS[9], ARGV[9], member)
          redis.call('ZADD', KEYS[10], ARGV[9], member)
          if ARGV[11] == '1' then redis.call('ZADD', KEYS[11], ARGV[10], member) end
          if mode == '0' then
            redis.call('SADD', KEYS[12], ARGV[12])
            redis.call('HSET', KEYS[13], ARGV[12], ARGV[13])
            redis.call('HINCRBY', KEYS[14], ARGV[12], 1)
          end
        end

        for _, row in ipairs(rows) do
          redis.call('ZADD', KEYS[15], row.score, row.messageId)
        end

        if mode ~= '2' then
          redis.call('HDEL', KEYS[1], ARGV[3])
        end
        return 1
        """;

    /// <summary>
    /// <c>RPUSH</c> the entry; the reply is the list length, which is the entry's per-instance sequence
    /// number by construction. <c>ARGV[2]</c> is the message id to add to the dedupe set, or empty for
    /// an entry type that must not count as a duplicate. Outside every persist script on purpose: the
    /// engine's redelivery net needs the append durable independently of the persist.
    /// </summary>
    public const string AppendLogEntry = """
        local sequence = redis.call('RPUSH', KEYS[1], ARGV[1])
        if ARGV[2] ~= '' then
          redis.call('SADD', KEYS[2], ARGV[2])
        end
        return sequence
        """;

    /// <summary>KEYS: row, due set, scope set. ARGV: padded id, due score, correlation id, saga type, state, due microseconds, scope key.</summary>
    public const string ScheduleTimeout = """
        redis.call('HSET', KEYS[1],
          'id', ARGV[1], 'correlationId', ARGV[3], 'sagaType', ARGV[4], 'forState', ARGV[5],
          'dueMicros', ARGV[6], 'status', '0', 'forKey', ARGV[7])
        redis.call('ZADD', KEYS[2], ARGV[2], ARGV[1])
        redis.call('SADD', KEYS[3], ARGV[1])
        return 1
        """;

    /// <summary>KEYS: scope set, due set. ARGV: row key prefix. Cancels every pending row in the scope, then drops the scope set.</summary>
    public const string CancelTimeouts = """
        local ids = redis.call('SMEMBERS', KEYS[1])
        for _, id in ipairs(ids) do
          local key = ARGV[1] .. id
          if redis.call('HGET', key, 'status') == '0' then
            redis.call('HSET', key, 'status', '2')
            redis.call('ZREM', KEYS[2], id)
          end
        end
        redis.call('DEL', KEYS[1])
        return #ids
        """;

    /// <summary>
    /// One round trip claims up to a batch of due timeouts, earliest-due first, marking each Fired and
    /// removing it from the due set and its scope set. KEYS: due set. ARGV: row key prefix, max score,
    /// batch size, whether a saga-type filter follows ('1'/'0'), then the wanted saga types. A row the
    /// filter rejects is skipped and stays Pending; the walk advances past what it skipped rather than
    /// stopping short, so a batch is filled whenever enough eligible rows exist.
    /// </summary>
    public const string ClaimDueTimeouts = """
        local due = KEYS[1]
        local prefix = ARGV[1]
        local maxScore = ARGV[2]
        local batch = tonumber(ARGV[3])
        local filtered = ARGV[4] == '1'
        local wanted = {}
        for i = 5, #ARGV do wanted[ARGV[i]] = true end

        local claimed = {}
        local skipped = 0
        while #claimed < batch do
          local ids = redis.call('ZRANGEBYSCORE', due, '-inf', maxScore, 'LIMIT', skipped, batch - #claimed)
          if #ids == 0 then break end
          for _, id in ipairs(ids) do
            local key = prefix .. id
            local row = redis.call('HMGET', key, 'correlationId', 'sagaType', 'forState', 'dueMicros', 'forKey')
            if row[1] and (not filtered or wanted[row[2]]) then
              redis.call('HSET', key, 'status', '1')
              redis.call('ZREM', due, id)
              if row[5] then redis.call('SREM', row[5], id) end
              claimed[#claimed + 1] = { id, row[1], row[2], row[3], row[4] }
            else
              skipped = skipped + 1
            end
          end
        end
        return claimed
        """;

    /// <summary>KEYS: row, pending set. ARGV: message id. The inline drain's path; a missing row is a no-op.</summary>
    public const string MarkOutboxDispatched = """
        if redis.call('EXISTS', KEYS[1]) == 1 then
          redis.call('HSET', KEYS[1], 'status', '1')
        end
        redis.call('ZREM', KEYS[2], ARGV[1])
        return 1
        """;

    /// <summary>
    /// One round trip claims up to a batch of pending outbox rows, earliest-created first. KEYS: pending
    /// set. ARGV: row key prefix, max score, batch size. Every member of the pending set is eligible, so
    /// the batch is one range read; each row is marked Dispatched, removed from the set and returned whole.
    /// </summary>
    public const string ClaimPendingOutbox = """
        local pending = KEYS[1]
        local prefix = ARGV[1]
        local ids = redis.call('ZRANGEBYSCORE', pending, '-inf', ARGV[2], 'LIMIT', 0, tonumber(ARGV[3]))
        local claimed = {}
        for _, id in ipairs(ids) do
          local key = prefix .. id
          redis.call('ZREM', pending, id)
          if redis.call('EXISTS', key) == 1 then
            redis.call('HSET', key, 'status', '1')
            claimed[#claimed + 1] = redis.call('HGETALL', key)
          end
        end
        return claimed
        """;

    /// <summary>Run at bootstrap to verify scripting works on the server, by running a script rather than parsing a version string.</summary>
    public const string Ping = "return 1";
}
