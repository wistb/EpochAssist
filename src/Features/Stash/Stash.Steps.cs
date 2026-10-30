using EpochAssist.Core;
using Il2Cpp;
using Il2CppLE.Data;
using Il2CppLE.UI.PanelSystem;
using UnityEngine;

namespace EpochAssist.Features;

internal static partial class Stash
{
    internal enum StepKind { NewFolder, ConfigureFolder, ConfigureTab, BuyTab, BuyCopy, ReorderFolder, ReorderTab }

    /// <summary>One game stash command, worked out from the preview of one read. Only the first step of a list is ever sent:
    /// every read plans again, so later steps name tabs and folders that may not exist yet.</summary>
    internal sealed class Step
    {
        public StepKind Kind { get; init; }
        public Target? Target { get; init; }
        public PlanFolder? Folder { get; init; }
        /// <summary>The existing tab or folder it acts on; null when it doesn't exist yet.</summary>
        public int? Id { get; init; }
        public int? CopyFrom { get; init; }
        /// <summary>A folder move's place among the folders, from 0.</summary>
        public int Place { get; init; }
        public long Gold { get; init; }
        /// <summary>The button's words.</summary>
        public string Says { get; init; } = string.Empty;
        /// <summary>The game's message for the step, for the page and the bridge.</summary>
        public string Sends { get; init; } = string.Empty;
        /// <summary>The group the step's game call is in.</summary>
        public string Group { get; init; } = string.Empty;
        /// <summary>A read that shows the step done.</summary>
        public Func<StashSnapshot, bool> Expect { get; init; } = _ => false;
    }

    /// <summary>
    /// The steps that turn the stash into the preview, in send order: folders to create, folders renamed, reused tabs
    /// configured (most useful first), purchases in rank order (each configured on a later step), tab reorders last.
    /// Folders are never moved (the game's own folder delete needs each folder's id to be its place). Never a delete, sort,
    /// merge or item move.
    /// </summary>
    public static List<Step> StepsFor(StashSnapshot snap, Preview preview)
    {
        var steps = new List<Step>();
        int folderCount = snap.Folders.Count;
        foreach (var f in preview.Folders.Where(f => f.ExistingId == null))
            steps.Add(new Step
            {
                Kind = StepKind.NewFolder, Folder = f, Says = $"Create folder {f.Name}", Sends = "the game's folder create (StashCategoryCreate)",
                Group = FolderGroup, Expect = s => s.Folders.Count > folderCount,
            });
        foreach (var f in preview.Folders.Where(f => f.Configure && f.ExistingId != null))
        {
            var now = snap.Folder(f.ExistingId!.Value)!;
            int id = now.Id;
            steps.Add(new Step
            {
                Kind = StepKind.ConfigureFolder, Folder = f, Id = id,
                Says = $"Folder {now.Name} → {f.Name} ({Changes(now.Name != f.Name, now.IconId != f.IconId, now.ColourId != f.ColourId)})",
                Sends = "the game's folder configure (StashCategoryConfigure)", Group = ConfigureGroup,
                Expect = s => s.Folder(id) is { } x && x.Name == f.Name && x.IconId == f.IconId && x.ColourId == f.ColourId,
            });
        }
        foreach (var x in preview.Targets.Where(x => x.Fill == Fill.Reuse && x.Folder.ExistingId != null).OrderBy(x => x.Tab.Rank).ThenBy(x => x.Order))
        {
            var now = snap.Tab(x.TabId!.Value)!;
            int id = now.Id, parent = x.InKeepFolder ? now.FolderId : x.Folder.ExistingId!.Value;
            var target = x;
            steps.Add(new Step
            {
                Kind = StepKind.ConfigureTab, Target = x, Id = id,
                Says = $"{now.Name} → {x.Tab.Name} ({string.Join(", ", x.Changes)})",
                Sends = "the game's tab configure (StashTabConfigure)", Group = ConfigureGroup,
                Expect = s => s.Tab(id) is { } t && t.Name == target.Tab.Name && t.IconId == target.Tab.IconId && t.ColourId == target.Tab.ColourId
                    && t.Rules.Equals(target.Rules) && t.FolderId == parent,
            });
        }
        foreach (var x in preview.Targets.Where(x => x.Fill == Fill.Buy).OrderBy(x => x.Price).ThenBy(x => x.Tab.Rank))
        {
            int count = snap.TabCount;
            // A section's extra tab, once its first is done, is bought as a copy of it (the game's Duplicate), on the client
            // copy only: the save file's copy makes a plain tab from it all the same. Its configure still follows (counted
            // in StepsLeft) until a copy is seen to land configured.
            var source = x.Copy > 1 && snap.ClientCopy ? CopySource(preview, x) : null;
            steps.Add(new Step
            {
                Kind = source != null ? StepKind.BuyCopy : StepKind.BuyTab, Target = x, Id = x.Folder.ExistingId, CopyFrom = source, Gold = x.Price,
                Says = source != null ? $"Buy a copy of {x.Tab.Name} · {x.Price:N0} gold" : $"Buy a tab for {x.Tab.Name} · {x.Price:N0} gold",
                Sends = source != null ? "the game's tab purchase with copy-from (Duplicate)" : "the game's tab purchase (StashTabPurchase)", Group = BuyGroup,
                Expect = s => s.TabCount > count,
            });
        }
        // Folder places, on the client copy only (the save file's copy never moves folders): the first place, from the top,
        // whose folder is wrong takes the folder that belongs there, so each misplaced folder costs one move.
        if (snap.ClientCopy)
        {
            var current = snap.Folders.Select(f => f.Id).ToList();
            var wanted = preview.Folders.OrderBy(f => f.Index).Where(f => f.ExistingId != null).Select(f => f.ExistingId!.Value).ToList();
            for (int i = 0; i < wanted.Count && i < current.Count; i++)
            {
                if (current[i] == wanted[i]) continue;
                int id = wanted[i], place = i;
                var f = preview.Folders.First(p => p.ExistingId == id);
                steps.Add(new Step
                {
                    Kind = StepKind.ReorderFolder, Folder = f, Id = id, Place = place, Says = $"Move folder {f.Name} to place {place + 1}",
                    Sends = "the game's folder reorder (StashCategoryReorder)", Group = MoveGroup,
                    Expect = s => s.Folders.FindIndex(x => x.Id == id) == place,
                });
                break;
            }
        }
        // Tab moves only once nothing else is left: until every tab is configured into its folder, a tab still to leave can
        // stand where a move would aim, and the move would be wasted.
        if (steps.Count > 0) return steps;
        foreach (var group in preview.Targets.Where(x => x.TabId != null && !x.InKeepFolder && x.Folder.ExistingId != null).GroupBy(x => x.Folder.ExistingId!.Value))
        {
            var inFolder = snap.TabsIn(group.Key).ToList();
            foreach (var x in group.OrderBy(x => x.Index))
            {
                int at = inFolder.FindIndex(t => t.Id == x.TabId);
                if (at < 0 || at == x.Index || snap.Tab(x.TabId!.Value)?.FolderId != group.Key) continue;
                int id = x.TabId!.Value, place = x.Index, folder = group.Key;
                steps.Add(new Step
                {
                    Kind = StepKind.ReorderTab, Target = x, Id = id,
                    Says = at > place ? $"Move {x.Tab.Name} up {at - place}" : $"Move {x.Tab.Name} down {place - at}",
                    Sends = "the game's tab reorder (StashTabReorder)", Group = MoveGroup,
                    Expect = s => s.TabsIn(folder).ToList().FindIndex(t => t.Id == id) == place,
                });
                break;
            }
        }
        return steps;
    }

    /// <summary>Why nothing is sent while the game has two folders or tabs on one id.</summary>
    public static string SharedIdsText(StashSnapshot snap) =>
        $"The game has {string.Join(", ", snap.SharedIds)} on one id, so a step could reach the wrong one. Nothing is sent until they differ.";

    // The section's first tab, when it is already exactly as planned.
    private static int? CopySource(Preview preview, Target x) =>
        preview.Targets.FirstOrDefault(y => y.Tab.Key == x.Tab.Key && y.Copy == 1 && y.Fill == Fill.Done && y.TabId != null)?.TabId;

    private static string Changes(bool name, bool icon, bool colour) =>
        string.Join(", ", new[] { name ? "name" : null, icon ? "icon" : null, colour ? "colour" : null }.OfType<string>());

    // ---- Sending ----

    internal enum ApplyState { Idle, Sending, Paused, Stopped }

    private static ApplyState s_state;
    private static string s_reason = string.Empty;
    private static Step? s_inFlight;
    private static float s_sentAt, s_resumeAt;
    private static bool s_all;
    private static int s_allDone, s_allTotal;
    private static ulong s_afterFingerprint;

    public static ApplyState State => s_state;
    public static string StopReason => s_reason;
    public static bool StepsBusy => s_state is ApplyState.Sending or ApplyState.Paused;
    public static bool RunningAll => s_all && StepsBusy;
    public static (int Done, int Total) AllProgress => (s_allDone, s_allTotal);
    public static string StepsStatus => s_all ? $"Step {s_allDone + 1} of {Math.Max(s_allTotal, s_allDone + 1)}" : "Waiting for the game…";

    /// <summary>The step being sent, or the pause before the next: its words, and how far the pause has run (0–1).</summary>
    public static (string Says, bool Waiting, float Pause) StepNow
    {
        get
        {
            if (s_state == ApplyState.Paused)
            {
                float span = Math.Max(0.01f, s_resumeAt - s_pausedAt);
                return (s_lastDone.Length > 0 ? "Done: " + s_lastDone : "Next step…", false, Mathf.Clamp01((Time.unscaledTime - s_pausedAt) / span));
            }
            return (s_inFlight?.Says ?? "Waiting for the game…", true, 0f);
        }
    }

    private static float s_pausedAt;
    private static string s_lastDone = string.Empty;

    private static void ResetSteps()
    {
        s_state = ApplyState.Idle;
        s_reason = string.Empty;
        s_inFlight = null;
        s_all = false;
        s_allDone = s_allTotal = 0;
    }

    /// <summary>The steps for the current read and preview; empty without either.</summary>
    public static List<Step> CurrentSteps()
    {
        if (s_snap == null || s_preview == null || s_preview.Fingerprint != s_snap.Fingerprint) return s_noSteps;
        // Worked out once per read and preview; the page asks every time it draws.
        if (s_stepsFor.Snap != s_snap || s_stepsFor.Preview != s_preview) s_stepsFor = (s_snap, s_preview, StepsFor(s_snap, s_preview));
        return s_stepsFor.Steps!;
    }

    private static readonly List<Step> s_noSteps = new();
    private static (StashSnapshot? Snap, Preview? Preview, List<Step>? Steps) s_stepsFor;

    /// <summary>
    /// Every step left to make the preview, counted from the whole plan rather than the steps sendable now: folders to
    /// create and their rename, folders renamed, every reused tab's configure (also those whose folder doesn't exist
    /// yet), each purchase and its configure, and the folder and tab moves the rest will leave.
    /// </summary>
    public static int StepsLeft(StashSnapshot snap, Preview preview)
    {
        // Asked every frame by the page; the answer only changes with the read or the plan.
        if (s_stepsLeft is { } c && ReferenceEquals(c.Snap, snap) && ReferenceEquals(c.Preview, preview)) return c.Count;
        int count = CountStepsLeft(snap, preview);
        s_stepsLeft = (snap, preview, count);
        return count;
    }

    private static (StashSnapshot Snap, Preview Preview, int Count)? s_stepsLeft;

    private static int CountStepsLeft(StashSnapshot snap, Preview preview) =>
        preview.Folders.Count(f => f.ExistingId == null) * 2
        + preview.Folders.Count(f => f.Configure && f.ExistingId != null)
        + preview.Targets.Count(x => x.Fill == Fill.Reuse)
        + preview.Targets.Count(x => x.Fill == Fill.Buy) * 2
        + (snap.ClientCopy ? FolderMovesAhead(snap, preview) : 0)
        + TabMovesAhead(snap, preview);

    // A planned folder's key: its id, or a stand-in below zero for one still to make.
    private static int FolderKey(Preview preview, PlanFolder f) => f.ExistingId ?? -1 - preview.Folders.IndexOf(f);

    // The folder moves StepsFor will make, one at a time: new folders come in last, in plan order, then the first place
    // from the top whose folder is wrong takes the folder that belongs there, as many times as that takes.
    private static int FolderMovesAhead(StashSnapshot snap, Preview preview)
    {
        var current = snap.Folders.Select(f => f.Id).ToList();
        current.AddRange(preview.Folders.Where(f => f.ExistingId == null).Select(f => FolderKey(preview, f)));
        var wanted = preview.Folders.OrderBy(f => f.Index).Select(f => FolderKey(preview, f)).ToList();
        int moves = 0;
        for (int i = 0; i < wanted.Count && i < current.Count && moves < 200; i++)
        {
            if (current[i] == wanted[i]) continue;
            current.Remove(wanted[i]);
            current.Insert(i, wanted[i]);
            moves++;
        }
        return moves;
    }

    // The tab moves StepsFor will make once nothing else is left. Where the steps before leave each tab is played through
    // first. On the save file's copy a tab configured into another folder, and a bought one, is moved straight to its
    // place (at most the folder's end), so the game's window never sees two tabs on one display order (its sort leaves
    // those in no fixed order). On the client copy a configured tab goes last in its new folder and a purchase in at its
    // place, or last in a folder with fewer tabs. Then the moves are counted as StepsFor makes them: the first tab, by
    // planned place, standing elsewhere moves to its place, as many times as that takes.
    private static int TabMovesAhead(StashSnapshot snap, Preview preview)
    {
        var folders = snap.Folders.ToDictionary(f => f.Id, f => snap.TabsIn(f.Id).Select(t => t.Id).ToList());
        foreach (var f in preview.Folders.Where(f => f.ExistingId == null)) folders[FolderKey(preview, f)] = new List<int>();
        var tabOf = new Dictionary<Target, int>();
        foreach (var x in preview.Targets.Where(x => x.TabId != null)) tabOf[x] = x.TabId!.Value;
        foreach (var x in preview.Targets.Where(x => x.Fill == Fill.Reuse && !x.InKeepFolder && x.Changes.Contains("folder"))
                     .OrderBy(x => x.Tab.Rank).ThenBy(x => x.Order))
        {
            int id = x.TabId!.Value;
            foreach (var list in folders.Values) list.Remove(id);
            if (!folders.TryGetValue(FolderKey(preview, x.Folder), out var into)) continue;
            if (snap.ClientCopy) into.Add(id);
            else into.Insert(Math.Min(x.Index, into.Count), id);
        }
        // A purchase stands in as a number below every id until it exists.
        int bought = -1;
        foreach (var x in preview.Targets.Where(x => x.Fill == Fill.Buy).OrderBy(x => x.Price).ThenBy(x => x.Tab.Rank))
        {
            if (!folders.TryGetValue(FolderKey(preview, x.Folder), out var into)) continue;
            into.Insert(Math.Min(x.Index, into.Count), --bought);
            tabOf[x] = bought;
        }
        int moves = 0;
        foreach (var group in preview.Targets.Where(x => !x.InKeepFolder && tabOf.ContainsKey(x)).GroupBy(x => FolderKey(preview, x.Folder)))
        {
            if (!folders.TryGetValue(group.Key, out var list)) continue;
            var ordered = group.OrderBy(x => x.Index).ToList();
            for (int guard = 0; guard < 200; guard++)
            {
                var wrong = ordered.FirstOrDefault(x => list.IndexOf(tabOf[x]) is int at && at >= 0 && at != x.Index);
                if (wrong == null) break;
                list.Remove(tabOf[wrong]);
                list.Insert(Math.Min(wrong.Index, list.Count), tabOf[wrong]);
                moves++;
            }
        }
        return moves;
    }

    /// <summary>Why the step can't be sent now; null when it can.</summary>
    public static string? Blocked(Step step, StashSnapshot snap)
    {
        if (snap.SharedIds.Count > 0) return SharedIdsText(snap);
        if (ClosingFolderWindow) return "Closing the game's folder window…";
        if (snap.ClientCopy && !s_all && !Bucket.Ready(step.Kind)) return "Waiting for the game's message limit…";
        if (!Has(step.Group)) return "The game changed this stash command; it isn't available.";
        if (step.Kind is StepKind.BuyTab or StepKind.BuyCopy)
        {
            if (snap.TabCount >= MaxTabs) return $"The stash holds at most {MaxTabs} tabs.";
            if (Gold() is not { } gold || !StashHelpers.CanBuy(gold, snap.TabCount, (int)step.Gold)) return $"Not enough gold for the next tab ({step.Gold:N0}).";
        }
        if (step.Kind == StepKind.NewFolder && snap.Folders.Count >= MaxFolders) return $"The stash holds at most {MaxFolders} folders.";
        if (step.Kind is StepKind.ReorderTab or StepKind.ReorderFolder && MoveBlocked() is { } cannot) return cannot;
        if (NeedsWindow(step) && !WindowOpen()) return "Open your stash to continue.";
        return null;
    }

    /// <summary>Apply: sends the first step once, against the read it was planned from.</summary>
    public static bool ApplyNext(out string message)
    {
        s_all = false;
        return SendFirst(out message);
    }

    /// <summary>Apply all: every step at this budget, each after the last one is confirmed and a pause.</summary>
    public static bool ApplyAll(out string message)
    {
        var steps = CurrentSteps();
        s_all = true;
        s_allDone = 0;
        s_allTotal = s_preview != null && s_snap != null ? StepsLeft(s_snap, s_preview) : steps.Count;
        if (!SendFirst(out message))
        {
            s_all = false;
            return false;
        }
        return true;
    }

    public static void StopAll(string reason)
    {
        if (!StepsBusy) return;
        Stop(reason);
    }

    private static void StopAllIfRunning(string reason)
    {
        // A single step already sent is still confirmed by the next read; only Apply all stops with the page.
        if (RunningAll) Stop(reason);
    }

    private static void Stop(string reason)
    {
        s_state = ApplyState.Stopped;
        s_reason = reason;
        s_inFlight = null;
        s_all = false;
        Log.Info($"Stash: stopped: {reason}");
    }

    private static bool SendFirst(out string message)
    {
        var snap = Read();
        if (snap == null || s_snap == null || s_preview == null)
        {
            message = "No stash read yet.";
            return false;
        }
        // Never send against a stash that changed since the preview was planned.
        if (snap.Fingerprint != s_preview.Fingerprint)
        {
            Refresh(force: true);
            message = "The stash changed; the preview is planned again. Press again to send.";
            return false;
        }
        var steps = StepsFor(snap, s_preview);
        if (steps.Count == 0)
        {
            message = "Your stash matches the preview.";
            s_state = ApplyState.Idle;
            return false;
        }
        var step = steps[0];
        // Apply all on the client copy: the first step in plan order whose message type has room (a single Apply sends
        // the step its button showed, or waits for its room).
        if (snap.ClientCopy && s_all)
        {
            // The first step in plan order whose message type has room on the server; with none, wait for the first
            // refill. A step blocked for another reason stops the run, as before.
            var ready = steps.FirstOrDefault(x => Blocked(x, snap) == null && Bucket.Ready(x.Kind));
            if (ready == null)
            {
                var waiting = steps.Where(x => Blocked(x, snap) == null).ToList();
                if (waiting.Count > 0)
                {
                    s_resumeAt = waiting.Min(x => Bucket.ReadyAt(x.Kind));
                    s_pausedAt = Time.unscaledTime;
                    s_state = ApplyState.Paused;
                    message = "Waiting for the game's message limit…";
                    return true;
                }
            }
            else step = ready;
        }
        if (Blocked(step, snap) is { } why)
        {
            Stop(why);
            message = why;
            return false;
        }
        if (step.Id is { } id && step.Kind is StepKind.ConfigureTab or StepKind.ReorderTab && snap.Tab(id) is { } tab && CurrentChoices.Of(tab) == TabChoice.Keep)
        {
            Stop("A kept tab can't be changed.");
            message = s_reason;
            return false;
        }
        try
        {
            Send(step, snap);
        }
        catch (Exception e)
        {
            Stop($"The game refused: {e.Message}");
            message = s_reason;
            return false;
        }
        if (snap.ClientCopy) Bucket.Take(step.Kind);
        s_inFlight = step;
        s_sentAt = Time.unscaledTime;
        // Read from the next frame on, not at the idle poll's next turn, so the game's answer is seen at once.
        s_nextRead = Time.unscaledTime;
        s_state = ApplyState.Sending;
        s_reason = string.Empty;
        s_afterFingerprint = snap.Fingerprint;
        message = $"Sent: {step.Says}";
        Log.Info($"Stash: sent {step.Kind}: {step.Says}");
        return true;
    }

    /// <summary>
    /// Sends one step through the same call Apply makes, without waiting for its confirmation; on the client copy only
    /// while its message type has room. For dev tools that make work for the page (the stash scramble). False when it has
    /// to wait.
    /// </summary>
    /// <summary>Whether the step's message type has room now on the client copy (always on the saved copy).</summary>
    internal static bool CanSendNow(Step step) => s_snap?.ClientCopy != true || Bucket.Ready(step.Kind);

    internal static bool SendNow(Step step, StashSnapshot snap)
    {
        if (snap.ClientCopy && !Bucket.Ready(step.Kind)) return false;
        Send(step, snap);
        if (snap.ClientCopy) Bucket.Take(step.Kind);
        return true;
    }

    // On the client copy each step calls the code the game's own button runs, which sends the game's own message; on the
    // saved copy it is that message's local command, which changes the stash in place and opens no window.
    private static void Send(Step step, StashSnapshot snap)
    {
        var ui = TabsUi() ?? throw new InvalidOperationException("the stash controls aren't loaded");
        switch (step.Kind)
        {
            case StepKind.NewFolder:
                if (snap.ClientCopy)
                {
                    // Here the + button's code only sends the create; no window opens.
                    ui.AddNewCategory();
                    s_closeFolderWindowUntil = Time.unscaledTime + 6f;
                    s_folderWindowCreatedAt = Time.unscaledTime;
                    s_folderWindowSeenAt = 0f;
                }
                else
                {
                    // Here the + button opens the folder window as well; the create's local command makes the folder alone,
                    // already named.
                    ServerStashCommands.ServerAddNewCategory(Manager(), StashIndex(), step.Folder!.Name);
                }
                break;
            case StepKind.ConfigureFolder:
            {
                var f = step.Folder!;
                var result = new StashConfigureTabModalResult
                {
                    SelectedTabId = step.Id!.Value, SelectedIconId = f.IconId, SelectedColorId = f.ColourId, SelectedParentId = step.Id!.Value,
                    TabName = f.Name, FolderPriority = snap.Folder(step.Id!.Value) is { Rules.IsEmpty: false } old ? Rules.ToGame(old.Rules) : null,
                    Action = StashConfigureResult.Save,
                };
                if (snap.ClientCopy) ui.HandleConfigureCategoryResult(result);
                // The window's handler only knows folders it has drawn; the local command works on the stash itself.
                else
                {
                    ServerStashCommands.ServerSetCategoryInfo(Manager(), StashIndex(), step.Id!.Value, f.IconId, f.ColourId, f.Name, result.FolderPriority);
                    // The window's own label update, which its configure handler runs; the data update skips folders it has.
                    s_windowFolder = (step.Id!.Value, f.IconId, f.ColourId, f.Name);
                }
                break;
            }
            case StepKind.ConfigureTab:
            {
                var x = step.Target!;
                var tab = snap.Tab(step.Id!.Value)!;
                var result = new StashConfigureTabModalResult
                {
                    SelectedTabId = tab.Id, SelectedIconId = x.Tab.IconId, SelectedColorId = x.Tab.ColourId,
                    SelectedParentId = x.InKeepFolder ? tab.FolderId : x.Folder.ExistingId!.Value, TabName = x.Tab.Name,
                    TabPriority = Rules.ToGame(x.Rules, forSend: true), IgnoreFolderPriority = tab.IgnoresFolderRules, Action = StashConfigureResult.Save,
                };
                if (snap.ClientCopy) ui.HandleConfigureTabResult(result);
                else
                {
                    ServerStashCommands.ServerSetTabInfo(Manager(), StashIndex(), tab.Id, x.Tab.IconId, x.Tab.ColourId, result.SelectedParentId, x.Tab.Name,
                        result.TabPriority, tab.IgnoresFolderRules);
                    s_windowTab = (tab.Id, x.Tab.IconId, x.Tab.ColourId, x.Tab.Name, result.SelectedParentId, result.TabPriority);
                    if (result.SelectedParentId != tab.FolderId) PlaceSaved(tab.Id, result.SelectedParentId, x.Index);
                }
                break;
            }
            case StepKind.BuyTab:
            case StepKind.BuyCopy:
            {
                var x = step.Target!;
                var manager = ItemContainersManager.Instance ?? throw new InvalidOperationException("no item containers");
                int order = x.Folder.ExistingId is { } fid ? snap.TabsIn(fid).Count() : 0;
                var copyFrom = step.CopyFrom is { } from ? GameTab(snap, from) ?? throw new InvalidOperationException("the tab to copy is gone") : null;
                // As the button does on the client copy; on the saved copy without the window's controls, which open the new
                // tab's configure window.
                if (snap.ClientCopy) manager.PurchaseNewStashTab(x.Folder.ExistingId ?? 0, Math.Min(order, x.Index), null, 0, 0, null, copyFrom, ui);
                else
                {
                    int bought = manager.PurchaseNewStashTab(x.Folder.ExistingId ?? 0, Math.Min(order, x.Index), null, 0, 0, null, copyFrom);
                    PlaceSaved(bought, x.Folder.ExistingId ?? 0, x.Index);
                }
                break;
            }
            default:
                if (snap.ClientCopy) SendMove(step, snap, ui);
                else if (step.Kind == StepKind.ReorderTab) ServerStashCommands.ServerMoveTab(Manager(), StashIndex(), step.Id!.Value, step.Target!.Index);
                // A folder never moves on the save file's copy (its folder delete needs each id at its place).
                else throw new InvalidOperationException("folders don't move here");
                break;
        }
        // The local commands change the stash, not the window: it is handed the stash as the game's own data
        // update hands it, so it shows the change.
        if (!snap.ClientCopy) ShowInWindow(ui);
    }

    private static float s_closeFolderWindowUntil, s_folderWindowSeenAt, s_folderWindowLookAt;
    // How long the folder window shows before its Cancel is pressed: pressed in its first frames, the game's panel
    // system throws partway through closing it and leaves it on screen with dead buttons.
    private const float FolderWindowSettle = 0.5f;
    // How long a folder create's window may take to show before the page stops waiting for it.
    private const float FolderWindowShows = 1.5f;
    private static float s_folderWindowCreatedAt;

    /// <summary>A folder create's window is still to be closed; Apply all waits for it.</summary>
    public static bool ClosingFolderWindow => s_closeFolderWindowUntil > 0f;

    // Each frame, until the folder window a folder create opened is closed.
    private static void FolderWindowFrame()
    {
        if (s_closeFolderWindowUntil <= 0f) return;
        if (Time.unscaledTime > s_closeFolderWindowUntil)
        {
            s_closeFolderWindowUntil = 0f;
            Log.Warn("Stash: the folder window a folder create opened didn't close; close it with its Cancel button");
            return;
        }
        // Looked for ten times a second: the search goes through every loaded window object.
        if (Time.unscaledTime < s_folderWindowLookAt) return;
        s_folderWindowLookAt = Time.unscaledTime + 0.1f;
        var modal = OpenFolderWindow();
        if (modal == null)
        {
            // A create that is only a message opens no window; one that hasn't shown by now won't.
            if (s_folderWindowSeenAt <= 0f && Time.unscaledTime > s_folderWindowCreatedAt + FolderWindowShows) s_closeFolderWindowUntil = 0f;
            s_folderWindowSeenAt = 0f;
            return;
        }
        if (s_folderWindowSeenAt <= 0f) s_folderWindowSeenAt = Time.unscaledTime;
        if (Time.unscaledTime - s_folderWindowSeenAt < FolderWindowSettle) return;
        try
        {
            modal.OnNoButtonClicked();
            s_closeFolderWindowUntil = 0f;
        }
        catch (Exception e)
        {
            // Tried again after another settle.
            s_folderWindowSeenAt = Time.unscaledTime;
            Log.ErrorThrottled("stash:folderwindow", $"Stash: the folder window's Cancel failed, trying again: {e.Message}", 10);
        }
    }

    private static StashConfigureTabModal? OpenFolderWindow()
    {
        foreach (var modal in Resources.FindObjectsOfTypeAll<StashConfigureTabModal>())
            if (Game.Alive(modal) && modal.gameObject.activeInHierarchy) return modal;
        return null;
    }

    /// <summary>Presses Cancel on an open folder window now (the probe's verb); true when one was open.</summary>
    public static bool CloseFolderWindow()
    {
        var modal = OpenFolderWindow();
        if (modal == null) return false;
        modal.OnNoButtonClicked();
        return true;
    }

    // Called a few times a second while a step waits or Apply all runs.
    private static void StepsFrame()
    {
        if (s_state == ApplyState.Paused)
        {
            if (Time.unscaledTime >= s_resumeAt && !ClosingFolderWindow && !SendFirst(out string message) && s_state != ApplyState.Stopped)
            {
                // Nothing left: Apply all is done.
                s_state = ApplyState.Idle;
                s_all = false;
                Log.Info($"Stash: Apply all finished: {message}");
            }
            return;
        }
        if (s_state != ApplyState.Sending || s_inFlight == null || s_snap == null) return;
        if (s_inFlight.Expect(s_snap))
        {
            Log.Info($"Stash: confirmed {s_inFlight.Kind}");
            s_lastDone = s_inFlight.Says;
            // A rename by the page's own step keeps the tab's or folder's choice.
            if (CurrentChoices.RememberNames(s_snap)) ChoicesFile.Save(s_snap.Key, CurrentChoices);
            // The budget is what is left to spend: each read plans again, so a purchase comes off it.
            if (s_inFlight.Gold > 0)
            {
                CurrentChoices.Budget = Math.Max(0, CurrentChoices.Budget - s_inFlight.Gold);
                ChoicesChanged();
            }
            s_inFlight = null;
            if (s_all)
            {
                s_allDone++;
                // Counted again from the plan as it stands, so the bar ends where the steps do.
                if (s_snap != null && s_preview != null)
                {
                    var plan = PlanNow(s_snap, CurrentPreset, CurrentChoices, CurrentChoices.Budget);
                    s_allTotal = s_allDone + StepsLeft(s_snap, plan);
                }
                s_state = ApplyState.Paused;
                s_pausedAt = Time.unscaledTime;
                // The next step follows at once: on the client copy each message type's bucket paces it (SendFirst); on
                // the saved copy nothing is sent.
                s_resumeAt = Time.unscaledTime;
            }
            else s_state = ApplyState.Idle;
            return;
        }
        if (s_snap.Fingerprint != s_afterFingerprint && Time.unscaledTime - s_sentAt > 1.5f)
        {
            Stop("The stash changed in a way this step doesn't explain.");
            return;
        }
        if (Time.unscaledTime - s_sentAt > 5f) Stop("The game didn't answer.");
    }

    // ---- The server's message limit ----

    /// <summary>
    /// The server's receive limit as the client can model it: a leaky bucket per stash message type (the network layer
    /// keeps one per message id), each holding a few messages and draining at the game's stash limit a second. A step
    /// goes out only while its type has room, so different types go out side by side and none goes over its own.
    /// </summary>
    internal static class Bucket
    {
        // Three in a row were seen applied (tab configures 0.2 s apart); the drain is the game's own stash limit, with a
        // margin.
        private const float Size = 3f, Margin = 0.05f;
        private static readonly Dictionary<StepKind, (float Level, float At)> s_levels = new();

        private static float Drain => 1f / (1f / Math.Max(1, PlayerSync.StashRateLimitPerSecond) + Margin);

        // The purchase and its copy are one message type.
        private static StepKind TypeOf(StepKind kind) => kind == StepKind.BuyCopy ? StepKind.BuyTab : kind;

        private static float Level(StepKind kind)
        {
            if (!s_levels.TryGetValue(TypeOf(kind), out var b)) return 0f;
            return Math.Max(0f, b.Level - (Time.unscaledTime - b.At) * Drain);
        }

        public static bool Ready(StepKind kind) => Level(kind) + 1f <= Size;

        public static float ReadyAt(StepKind kind) => Time.unscaledTime + Math.Max(0f, Level(kind) + 1f - Size) / Drain;

        public static void Take(StepKind kind) => s_levels[TypeOf(kind)] = (Level(kind) + 1f, Time.unscaledTime);
    }

    // ---- The game's stash controls ----

    private static StashTabbedUIControls? TabsUi()
    {
        StashTabbedUIControls? pick = null;
        foreach (var c in Resources.FindObjectsOfTypeAll<StashTabbedUIControls>())
        {
            if (!Game.Alive(c) || c.gameObject.scene.name == null) continue;
            if (pick == null || c.gameObject.activeInHierarchy) pick = c;
        }
        return pick;
    }

    // The game's own tab object for an id, from the copy the snapshot came from.
    private static StashTab? GameTab(StashSnapshot snap, int id)
    {
        if (snap.ClientCopy)
        {
            var tabs = (LocalPlayer.instance ?? PlayerFinder.localPlayer)?.stashInfo?.tabs;
            if (tabs != null)
                for (int i = 0; i < tabs.Count; i++)
                    if (tabs[i].TabId == id) return tabs[i];
            return null;
        }
        var saved = PlayerFinder.getGlobalDataTracker()?.stash?.Tabs;
        if (saved != null)
            for (int i = 0; i < saved.Count; i++)
                if (saved[i].TabId == id) return saved[i];
        return null;
    }

    private static ItemContainersManager Manager() => ItemContainersManager.Instance ?? throw new InvalidOperationException("no item containers");

    private static (int Id, int Icon, int Colour, string Name)? s_windowFolder;
    private static (int Id, int Icon, int Colour, string Name, int Parent, StashPriority? Rules)? s_windowTab;

    // On the save file's copy, a tab just configured into a folder or bought is moved to its planned place (at most the
    // folder's end) by the game's own local tab move, which numbers the folder's tabs afresh: the configure keeps the
    // tab's old display order and the window's refresh would sort a tie into no fixed order.
    private static void PlaceSaved(int tabId, int folder, int place)
    {
        var tabs = PlayerFinder.getGlobalDataTracker()?.stash?.Tabs;
        if (tabs == null) return;
        int count = 0;
        for (int i = 0; i < tabs.Count; i++)
            if (tabs[i].CategoryId == folder) count++;
        if (count > 0) ServerStashCommands.ServerMoveTab(Manager(), StashIndex(), tabId, Math.Min(place, count - 1));
    }

    private static void ShowInWindow(StashTabbedUIControls ui)
    {
        try
        {
            var saved = PlayerFinder.getGlobalDataTracker()?.stash;
            if (saved?.Tabs == null || saved.Categories == null) return;
            var tabs = new Il2CppSystem.Collections.Generic.List<StashTab>();
            for (int i = 0; i < saved.Tabs.Count; i++) tabs.Add(saved.Tabs[i]);
            ui.OnDataUpdated(saved.Categories, tabs);
            if (s_windowFolder is { } f) ui.SetCategoryUIWithIconProperties(f.Id, f.Icon, f.Colour, f.Name);
            if (s_windowTab is { } t) ui.SetTabUIWithIconProperties(t.Id, t.Icon, t.Colour, t.Name, t.Parent, t.Rules);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("stash:window", $"Stash: the stash window didn't take the change: {e.Message}", 30);
        }
        finally
        {
            s_windowFolder = null;
            s_windowTab = null;
        }
    }

    private static int StashIndex() => ItemContainersManager.Instance?.stash?._CurrentStashIndex_k__BackingField ?? 0;

    /// <summary>Whether the game's stash window is open; steps wait for it.</summary>
    internal static bool StashWindowOpen => WindowOpen();

    private static bool WindowOpen() => UIBase.instance is { } ui && Game.Alive(ui) && ui.IsStashOpen();
}
