# Installs the recommended frontend tooling at the repository level. Run from the repository root, in PowerShell 7+.
# Afterwards create the local, git-ignored .mcp.json from .mcp.json.example and set CONTEXT7_API_KEY
# (docs/frontend.md, "Local MCP configuration"). Step 5 can write a literal key into .mcp.json: replace it
# with the ${CONTEXT7_API_KEY} reference before you commit anything.

# 1. Install UI UX Pro Max
npx -y ui-ux-pro-max-cli init --ai claude

# 2. Add Anthropic's official Claude Code plugin marketplace
claude plugin marketplace add anthropics/claude-plugins-official

# 3. Install the Frontend Design plugin
claude plugin install frontend-design@claude-plugins-official --scope project

# 4. Register Playwright MCP for this project
claude mcp add --scope project playwright npx @playwright/mcp@latest

# 5. Install Context7 for up-to-date documentation
npx -y ctx7 setup --claude --project --yes