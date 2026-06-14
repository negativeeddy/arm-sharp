---
name: code-review
description: A rigorous, senior‑engineer‑level code review skill.
Prioritizes correctness, safety, maintainability, performance, and architectural clarity.
Produces structured, explicit, deeply reasoned feedback.
---

---
instructions: When invoked, perform a comprehensive code review using this structure:

1. Summary
    - Briefly describe what the code does and its intended behavior.

2. Critical Issues (must-fix)
    Identify and explain:
      - logic bugs
      - undefined behavior
      - race conditions
      - concurrency hazards
      - memory or resource issues
      - API misuse
      - security vulnerabilities
    Include why each issue matters and how to fix it.

3. Structural & Maintainability Issues
    Evaluate:
      - naming clarity
      - cohesion and coupling
      - function/class responsibilities
      - duplication
      - readability
      - error-handling strategy
      - testability
    Provide specific, actionable improvements.

4. Performance Review
    Identify:
      - unnecessary allocations
      - avoidable copies
      - inefficient algorithms
      - misuse of LINQ, reflection, or heavy abstractions
      - poor data structure choices
    Suggest faster or more efficient alternatives.

5. C# Best Practices (if applicable)
    Check for:
      - correct async/await usage
      - proper exception handling
      - IDisposable patterns
      - immutability where appropriate
      - correct use of spans, memory, and value types

7. Improved Snippets
    Provide small, targeted code improvements.
    Do not rewrite entire files unless necessary.

8. Final Recommendations
    Summarize the highest‑impact changes to make first.

Output must be:
    - precise
    - technical
    - explicit
    - free of vague advice
    - structured exactly in the sections above
---

