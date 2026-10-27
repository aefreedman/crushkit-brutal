# Bounded transient tool feedback

`ToolFeedbackBuffer` is an opt-in owner-thread event model, not a global toast service or persistent error store. It has no window, clock, file, renderer or simulation ownership. `ToolFeedbackView.DrawNotice` is optional inline presentation inside the application's current ImGui window; it does not manage frames, focus, contexts or model lifetime. The existing [inspector callback contract](imgui-inspector.md) still applies.

## Explicit event and time contract

Construct `new ToolFeedbackBuffer(capacity, maxTextCharacters)` from validated application preferences. Both limits are required: capacity accepts 1–1,024 and the per-notice combined ID/title/detail length accepts 2–65,536 UTF-16 characters (at least a one-character ID and title). These are implementation ceilings, not application defaults. Text is validated before allocating private UTF-8 encodings; native NUL truncation and malformed UTF-16 are rejected. IDs must be nonempty, non-whitespace, case-sensitive stable strings without NUL or native `##` label delimiters. Titles must be nonempty/non-whitespace; details may be empty or multiline.

Call `Post(id, severity, title, detail, nowSeconds, durationSeconds)` only when an owner-update operation produces an event, not every frame that a status string remains present. It returns an immutable `ToolFeedbackNotice` with ID, severity, title, detail, creation time and expiry time. A consumer may retain that publication: coalescing, expiry, dismissal, eviction and clearing never mutate it. Neither private storage nor cached UTF-8 arrays escape.

Time is supplied by the host and must be finite, nonnegative and nondecreasing across all `Post`/`Advance` calls. Duration must be positive and produce a finite deadline strictly after the supplied time; durations smaller than that timestamp's representable resolution are rejected. No wall clock or hidden stopwatch is used. `Advance(nowSeconds)` removes notices at or beyond their deadlines and returns the number removed. Call it from the owner update even while the inspector is hidden. `Clear()` removes notices but does not reset the clock epoch; recreate the buffer if the host changes its clock epoch.

Ordering and saturation are deliberate:

- Index `0` is oldest; `Count - 1` is newest.
- Posting removes expired notices first. Reposting the same ordinal ID replaces it, moves it to newest and restarts its deadline. It is an explicit new event, not deduplication of identical frame polling.
- When full, a new ID evicts the oldest notice regardless of presentation severity. There is no deferred queue or later burst.
- `Dismiss(id)` removes that ID and returns whether it existed. Missing IDs are harmless. `Clear()` releases all active references.
- `Info`, `Success`, `Warning` and `Error` are presentation labels, not persistence or priority policy. A rejected edit, source conflict, unsaved change or pending reset/relaunch remains application-owned persistent state and must stay visible independently of expiring feedback.

An invalid post is transactional: limits, time, severity, deadline and encoding finish validating before the buffer clock or active notices change.

## Inline view and application ownership

`ToolFeedbackView.DrawNotice(notice, dismissible)` draws a wrapped severity-labeled title, optional detail and an optional visible **Dismiss** button. The action wraps below the heading when it cannot fit. It returns dismissal intent only; it neither consumes the model nor expires notices during drawing. Its native ID scope always unwinds. Applications may wrap each notice in their own semantic tone/style; no extra font, icon atlas, overlay window or theme singleton is needed. If displaying the same notice twice in one parent window, use distinct application ID scopes.

For example, keep the model and pending dismissal in an application object:

```csharp
private readonly ToolFeedbackBuffer feedback; // Construct with explicit JSON limits.
private string? pendingDismissal;

private void UpdateFeedback(double nowSeconds)
{
    if (pendingDismissal is { } id)
    {
        feedback.Dismiss(id);
        pendingDismissal = null;
    }
    feedback.Advance(nowSeconds);
}

private void SaveCompleted(double nowSeconds, double noticeSeconds)
{
    // Called once for the actual owner-update result, never from DrawControls.
    feedback.Post("configuration.save", ToolFeedbackSeverity.Success,
        "Selected sources saved", "Runtime apply and source save remain separate.",
        nowSeconds, noticeSeconds);
}

private void DrawFeedback()
{
    for (int i = 0; i < feedback.Count; i++)
    {
        var notice = feedback[i];
        if (ToolFeedbackView.DrawNotice(notice, dismissible: true))
            pendingDismissal = notice.Id;
    }
    // Persistent rejection/conflict/pending messages are drawn separately by the application.
}
```

This preserves iteration identity and keeps posting/expiry/dismissal at the owner boundary. No filesystem, configuration validation, command execution or simulation work belongs inside this view.

## Work and storage bounds

The private reference array has exactly the explicit capacity. Posting allocates one independently owned notice and its bounded text encodings at the event boundary. Coalescing, expiry and oldest eviction use linear scans/compaction over active notices, never reserved population products. `Advance`, indexing, dismissal and clearing allocate no per-frame arrays or publications. Expired/removed slots are cleared so only active notices remain retained by the model; independently retained publications remain the consumer's responsibility. Drawing visits only the notices the application chooses to display and reuses event-cached UTF-8 text. It does not drain/rebuild a collection, sort, sample producers, post events or advance time.

## Published API audit and evidence boundary

A disposable net10.0 library probe used exact `Ahwoo.BRUTAL.ImGui [2026.10.3]` from `https://api.nuget.org/v3/index.json`, with the repository SDK pin. Restore and both native-call/method-group compile checks succeeded with zero warnings/errors; no probe executable/native code was run. Relevant locked dependencies include ImGui.Native/Localization 2026.10.3, Core 2026.10.4, Numerics/Memory/Strings 2026.10.2 and CommunityToolkit.HighPerformance 8.4.2.

| Capability | Published evidence | Decision / gap |
| --- | --- | --- |
| Inline wrapped text and dismissal | Public `Brutal.ImGuiApi.ImGui.TextWrapped(ImString)`, `Button(ImString, in float2?)`; typed delegates plus direct span-label calls compiled against ImGui 2026.10.3 | Reuse native text/button, with the existing kit action-flow helper; no custom widget. |
| Stable native scope | Public `Brutal.ImGuiApi.ImGui.PushID(ImString)` and `PopID()`; typed delegates and direct calls compiled against the same package | Reuse native ID stack with `try/finally`; caller supplies stable identity. |
| Bounded event coalescing and host-time expiry | Read-only PE metadata inspection of published ImGui/Backends 2026.10.3, Core 2026.10.4 and Collections 2026.10.2 found no notification/feedback/toast or bounded/ring/queue type names | A small managed owner model supplies this application-facing contract. This is a scoped symbol inventory, not a claim that every BRUTAL package was searched. |

The focused CPU suite verifies limits/transactional validation, monotonic time and representable deadlines, deadline expiry, survivor order, same-ID coalescing, saturation, dismissal, retained immutable publications, bounded steady-state allocations and actual native Dismiss clicks at wide/narrow widths. Native view tests use CPU ImGui contexts only and verify ID scope balance and no model consumption. They do not establish real GLFW input, GPU presentation, visible color/contrast, DPI fidelity or focus behavior.
