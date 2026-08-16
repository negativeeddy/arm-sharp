---
name: code-review-fix
description: 'Work through open code review issues from the GitHub issue board. Use when: fixing code review findings, working through cleanup tasks, resolving code-review labeled issues, running periodic code review fixes, addressing technical debt.'
argument-hint: '[count|priority|issue#] — e.g. "3", "critical", "#82", "medium+low"'
---

# Code Review Fix Worker

Picks up open `code-review` labeled issues from the GitHub issue board and implements fixes one at a time, in priority order. Designed to be run periodically (e.g., weekly) to work through accumulated code review findings.

## When to Use

- Periodic cleanup sessions (weekly/biweekly)
- When the user says "fix some code review items"
- When the user specifies an issue number or priority to work on
- Sprint tech-debt reduction

## Procedure

### Step 1: Determine What to Fix

Parse the argument hint to decide which issues to work on:

| Argument | Behavior |
|----------|----------|
| (none) | Pick the highest-priority open `code-review` issue |
| `N` (number) | Fix the next N issues in priority order |
| `critical` | Fix all `priority: critical` issues |
| `medium` | Fix all `priority: medium` issues |
| `low` | Fix all `priority: low` issues |
| `#NNN` | Fix the specific issue number |
| `investigation` | Work on `needs-investigation` issues (deep reviews) |
| `medium+low` | Fix all medium and low priority issues |

Fetch open issues:

```bash
gh issue list --repo negativeeddy/arm-sharp \
  --label code-review --state open \
  --limit 200 --json number,title,labels
```

Sort by priority: critical → medium → low. Skip `needs-investigation` issues unless explicitly requested (they need review first, not a blind fix).

### Step 2: Create a Working Branch

```bash
git checkout -b fix/code-review-<batch-date>
# or
git checkout -b fix/code-review-#<issue-number>
```

### Step 3: For Each Issue

#### 3a. Read the Issue

```bash
gh issue view <number> --repo negativeeddy/arm-sharp --json title,body,labels
```

#### 3b. Understand the Problem

- Read the affected files mentioned in the issue
- Understand the current behavior
- Review the proposed fix

#### 3c. Implement the Fix

- Make the code changes following the proposed fix in the issue
- If the proposed fix has multiple options, implement the recommended one
- If the fix requires more investigation than expected, comment on the issue and skip:
  ```bash
  gh issue comment <number> --repo negativeeddy/arm-sharp \
    --body "Investigation reveals this needs more analysis: <explanation>. Converting to needs-investigation."
  gh issue edit <number> --repo negativeeddy/arm-sharp --add-label "needs-investigation"
  ```

#### 3d. Verify the Fix

- Run the build: `dotnet build ArmRipper.slnx -c Debug`
- Run relevant tests: `dotnet test` (scoped to affected project if possible)
- If no tests exist for the changed code, consider adding one

#### 3e. Commit and Reference

```bash
git add <files>
git commit -m "fix: <short description> (closes #<number>)"
```

The `closes #<number>` will auto-close the issue when merged.

#### 3f. Move to Next Issue

Repeat 3a–3e for the next issue in the batch.

### Step 4: Create Pull Request

After completing the batch:

```bash
gh pr create --repo negativeeddy/arm-sharp \
  --title "fix: Code review batch — <date>" \
  --body "## Code Review Fixes

Automated fixes from periodic code review cleanup.

### Issues Addressed
- [x] #N — <title>
- [x] #M — <title>
- [ ] #P — <title> (skipped: needs investigation)

### Changes
<brief summary of each fix>

### Testing
- All existing tests pass
- <any new tests added>"
```

### Step 5: Report Summary

Print a summary:

```
## Code Review Fix Session Complete

**Branch:** fix/code-review-<date>
**Issues Fixed:** N
**Issues Skipped:** N (needs investigation)
**PR:** <url>

### Fixed
| Issue | Title | Priority |
|-------|-------|----------|
| #N | <title> | 🔴 Critical |
| #M | <title> | 🟡 Medium |

### Skipped
| Issue | Title | Reason |
|-------|-------|--------|
| #P | <title> | Needs deeper investigation |
```

## Handling `needs-investigation` Issues

These issues require a deep review before a fix can be prescribed. When working on them:

1. **Read the investigation tasks** listed in the issue
2. **Explore the code** using read-only subagents
3. **Determine** if the code is actually robust (close the issue) or has real bugs
4. If bugs found:
   - Create a new sub-document in `docs/code-review/` with findings
   - Either fix directly or create new focused issues
   - Comment on the original issue with findings
5. If code is robust:
   - Comment with evidence of why it's safe
   - Close the issue: `gh issue close <number> --repo negativeeddy/arm-sharp`

## Safety Rules

- **Never modify running services** — only build and test
- **One fix per commit** — easy to revert individual fixes
- **Always run tests** before committing
- **Skip if uncertain** — mark as needs-investigation and move on
- **Respect the priority order** — critical first, then medium, then low
- **Don't batch unrelated fixes** — keep each issue's fix isolated

## Quick Reference Commands

```bash
# List open code-review issues
gh issue list --repo negativeeddy/arm-sharp --label code-review --state open

# View a specific issue
gh issue view <number> --repo negativeeddy/arm-sharp

# Comment on an issue
gh issue comment <number> --repo negativeeddy/arm-sharp --body "..."

# Close an issue
gh issue close <number> --repo negativeeddy/arm-sharp

# Create a PR
gh pr create --repo negativeeddy/arm-sharp --title "..." --body "..."
```
