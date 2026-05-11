---
description: Six Round Agent Review
name: Six Round Review
---

# Six Round Review instructions

---
mode: ask
description: Run a 6-lens code review of recent changes using specialized subagents for security, UX, best practices, data integrity, documentation, and test coverage
---

# Six-Model Code Review

Run 6 parallel review passes over the recent work, each focused on a different expertise area. Use `git diff` to identify the changed files, then dispatch 6 subagents (use the `Explore` agent for each) with the review instructions below.

## Setup

1. Identify the diff range. Ask the user or infer from recent commits:
   - `git --no-pager diff HEAD~N..HEAD --stat` (where N = number of commits in this work)
   - Or diff against a specific base: `git --no-pager diff origin/staging..HEAD --stat`
2. List the changed files and their purpose so each reviewer has context.
3. Run all 6 reviews in parallel via subagents.

## Review Lenses

### 1. Security (OWASP / Auth / Injection)

**Focus**: OWASP Top 10, auth bypass, injection, price manipulation, CSRF, rate limiting, information disclosure.

**Check for**:
- OData/SQL injection in any query construction (are GUIDs validated via assertGuid?)
- Client-side price/amount values that the backend trusts without recalculating
- Missing CSRF or auth checks on new/modified endpoints
- Missing rate limiting on new endpoints
- Secrets or tokens in responses, logs, or error messages
- Integer overflow or negative value edge cases in financial calculations

### 2. UX / Accessibility

**Focus**: User-facing clarity, accessibility, i18n, error states, loading states.

**Check for**:
- Are new UI elements/labels clear to non-technical users (parents, admins)?
- Accessibility: new form controls have labels, ARIA attributes, keyboard nav
- i18n: are all user-visible strings in translation files? Are Spanish translations accurate?
- Graceful degradation when API calls fail or return unexpected data
- Mobile responsiveness of new UI elements
- Consistent styling with existing patterns (Tailwind classes, color scheme)

### 3. Best Practices / Code Quality

**Focus**: Patterns, naming, DRY, separation of concerns, backward compatibility.

**Check for**:
- Does new code follow existing codebase patterns (BFF, data flow, naming)?
- Constants in the right module? Shared logic not duplicated between frontend/backend?
- Clean function signatures (no code smells like private properties on shared objects)?
- Backward compatibility maintained for modified functions?
- useMemo/useEffect dependency arrays correct in React components?
- N+1 query patterns or unnecessary Dataverse round-trips?
- Error handling: are new try/catch blocks consistent with existing patterns?

### 4. Data Integrity / Business Logic

**Focus**: Correctness of calculations, race conditions, edge cases, Stripe integration.

**Check for**:
- Do the calculations match the documented pricing scenarios exactly?
- Race conditions: concurrent registrations, stale counts, double-charges
- Stripe coupon/checkout amounts match backend calculations
- Payment items record correct pre/post-discount amounts
- Edge cases: zero-amount registrations, discount > subtotal, waitlisted campers
- Dataverse query filters correct (status exclusions, role filters)
- Withdrawal/cancellation impact on counts and pricing

### 5. Documentation

**Focus**: Accuracy, completeness, consistency across PRD and technical docs.

**Check for**:
- Do docs accurately describe the code behavior (no stale references)?
- Are all price/rate values updated consistently (no leftover old values)?
- New features documented: API response changes, new fields, new behaviors
- Schema docs match actual Dataverse schema
- PRD version and changelog updated if applicable
- Implementation docs step numbering correct after insertions

### 6. Test Coverage

**Focus**: Test adequacy, coverage gaps, missing edge cases.

**Check for**:
- Are all documented pricing scenarios covered by unit tests?
- New function signatures tested with all parameter variations?
- Legacy behavior still tested (backward compatibility)?
- What's NOT tested that should be: integration tests, frontend component tests, error paths, edge cases
- Mock patterns consistent with existing test infrastructure?
- Any test that would catch a regression if the sibling discount constant changed?

## Output Format

For each lens, report findings in priority order:

| Severity | Meaning |
|----------|---------|
| CRITICAL | Must fix before merge — security vuln, data corruption, money bug |
| HIGH | Should fix before merge — significant logic error, broken UX |
| MEDIUM | Fix soon — code smell, missing test, unclear docs |
| LOW | Nice to have — minor improvement, style nit |
| INFO | Observation — no action needed, just awareness |

After all 6 reports, provide a **consolidated action list** of items to fix, sorted by severity, with file references. Skip items that are pre-existing (not introduced by this diff).

## Post-Review Workflow

After presenting findings, execute these steps for every CRITICAL, HIGH, and MEDIUM item:

1. **Open a GitHub issue** for each finding (or group tightly related findings into one issue). Title format: `[six-lens] <severity>: <short description>`. Label: `code-review`. Include the lens name, file references, and concrete fix description.
2. **Fix the issue** — implement the change, run relevant tests.
3. **Close the GitHub issue** when the fix is committed. Reference the issue in the commit message.
4. **Document in commit** — commit message must reference the issue number and describe what was fixed.

For LOW and INFO items, list them in a single "housekeeping" issue for future reference — do not block the merge on them.
