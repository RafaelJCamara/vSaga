namespace VSaga.Transport.Http.Tests;

/// <summary>
/// The route table is a pure function of config, so it is worth pinning directly rather than only
/// through the transport: the wildcard fallback and the drop-unknown-endpoint-name rule are both
/// silent behaviours -- they turn a config mistake into "the message went somewhere else" or "the
/// message went nowhere" rather than into an error.
/// </summary>
public sealed class ConfigHttpRouteTableTests
{
    /// <summary>
    /// Fills HttpTransportOptions' own dictionaries rather than taking pre-built ones, so every case
    /// here inherits the exact comparers production config uses.
    /// </summary>
    private static ConfigHttpRouteTable RouteTable(
        (string Name, string Url)[]? endpoints = null,
        (string MessageType, string[] EndpointNames)[]? routes = null)
    {
        var options = new HttpTransportOptions();

        foreach (var (name, url) in endpoints ?? [])
            options.Endpoints[name] = url;

        foreach (var (messageType, endpointNames) in routes ?? [])
            options.Routes[messageType] = endpointNames;

        return new ConfigHttpRouteTable(options);
    }

    [Fact]
    public void ResolveRemoteEndpoints_ResolvesEachEndpointNameInOrder()
    {
        var table = RouteTable(
            endpoints: [("payments", "http://payments:8080"), ("shipping", "http://shipping:8080")],
            routes: [("OrderPlaced", ["payments", "shipping"])]);

        Assert.Equal(["http://payments:8080", "http://shipping:8080"], table.ResolveRemoteEndpoints("OrderPlaced"));
    }

    [Fact]
    public void ResolveRemoteEndpoints_YieldsNothingForATypeWithNoRouteAndNoWildcard()
    {
        var table = RouteTable(
            endpoints: [("payments", "http://payments:8080")],
            routes: [("OrderPlaced", ["payments"])]);

        Assert.Empty(table.ResolveRemoteEndpoints("SomethingElse"));
    }

    [Fact]
    public void ResolveRemoteEndpoints_FallsBackToTheWildcardForATypeWithNoExplicitEntry()
    {
        var table = RouteTable(
            endpoints: [("hub", "http://hub:8080")],
            routes: [(ConfigHttpRouteTable.WildcardRoute, ["hub"])]);

        Assert.Equal(["http://hub:8080"], table.ResolveRemoteEndpoints("AnyMessageAtAll"));
    }

    [Fact]
    public void ResolveRemoteEndpoints_PrefersAnExplicitEntryOverTheWildcard()
    {
        var table = RouteTable(
            endpoints: [("payments", "http://payments:8080"), ("hub", "http://hub:8080")],
            routes: [("OrderPlaced", ["payments"]), (ConfigHttpRouteTable.WildcardRoute, ["hub"])]);

        Assert.Equal(["http://payments:8080"], table.ResolveRemoteEndpoints("OrderPlaced"));
        Assert.Equal(["http://hub:8080"], table.ResolveRemoteEndpoints("OrderCancelled"));
    }

    /// <summary>
    /// Subtle, because it rests on the order of the lookup: the table short-circuits on TryGetValue
    /// succeeding before its Count == 0 check, so the wildcard is never consulted. An explicit empty
    /// list is how you say "this one type goes nowhere" while a wildcard covers everything else, so it
    /// has to stay an opt-out rather than a fall-through.
    /// </summary>
    [Fact]
    public void ResolveRemoteEndpoints_TreatsAnExplicitEmptyListAsNoEndpoints_NotAsAMissThatFallsThroughToTheWildcard()
    {
        var table = RouteTable(
            endpoints: [("hub", "http://hub:8080")],
            routes: [("OrderPlaced", []), (ConfigHttpRouteTable.WildcardRoute, ["hub"])]);

        Assert.Empty(table.ResolveRemoteEndpoints("OrderPlaced"));
    }

    [Fact]
    public void ResolveRemoteEndpoints_DropsARouteEntryNamingAnUnconfiguredEndpoint()
    {
        var table = RouteTable(
            endpoints: [("payments", "http://payments:8080")],
            routes: [("OrderPlaced", ["payments", "typo-in-this-name"])]);

        Assert.Equal(["http://payments:8080"], table.ResolveRemoteEndpoints("OrderPlaced"));
    }

    [Fact]
    public void ResolveRemoteEndpoints_YieldsNothingWhenEveryEndpointNameIsUnknown()
    {
        var table = RouteTable(
            endpoints: [("payments", "http://payments:8080")],
            routes: [("OrderPlaced", ["nope", "also-nope"])]);

        Assert.Empty(table.ResolveRemoteEndpoints("OrderPlaced"));
    }

    [Fact]
    public void ResolveRemoteEndpoints_YieldsNothingWhenNothingIsConfiguredAtAll() =>
        Assert.Empty(RouteTable().ResolveRemoteEndpoints("OrderPlaced"));

    [Fact]
    public void ResolveEndpointByName_ResolvesAConfiguredName()
    {
        var table = RouteTable(endpoints: [("payments", "http://payments:8080")]);

        Assert.Equal("http://payments:8080", table.ResolveEndpointByName("payments"));
    }

    [Fact]
    public void ResolveEndpointByName_IsNullForAnUnconfiguredName()
    {
        var table = RouteTable(endpoints: [("payments", "http://payments:8080")]);

        Assert.Null(table.ResolveEndpointByName("shipping"));
    }

    /// <summary>
    /// SendAsync's destination is an endpoint name, never a Routes key -- §4.3's
    /// AMQP-default-exchange analogue bypasses routing entirely, so a name that only exists in Routes
    /// is not addressable.
    /// </summary>
    [Fact]
    public void ResolveEndpointByName_DoesNotConsultRoutes()
    {
        var table = RouteTable(
            endpoints: [("payments", "http://payments:8080")],
            routes: [("OrderPlaced", ["payments"])]);

        Assert.Null(table.ResolveEndpointByName("OrderPlaced"));
    }

    [Fact]
    public void ResolveEndpointByName_DoesNotTreatTheWildcardAsAnAddressableDestination()
    {
        var table = RouteTable(
            endpoints: [("hub", "http://hub:8080")],
            routes: [(ConfigHttpRouteTable.WildcardRoute, ["hub"])]);

        Assert.Null(table.ResolveEndpointByName(ConfigHttpRouteTable.WildcardRoute));
    }
}
