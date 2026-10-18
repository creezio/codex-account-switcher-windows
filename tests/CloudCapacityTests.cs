using System;
using System.IO;
using System.Linq;
using System.Text;
using Creezio.Switcher;

internal static class CloudCapacityTests
{
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected invalid Cloud capacity rejection");
    }
    private static CloudJob Job(string id, string account, string state = "running")
    { return new CloudJob { Id = id, AccountKey = account, State = state, ThreadId = state == "queued" ? null : "native-" + id }; }
    internal static void RunAll(Action<string, Action> check, string root)
    {
        string folder = Path.Combine(root, "cloud-capacity");
        check("cloud capacity preserves two slots on existing installs without creating a preference", () => {
            var store = new RelayStore(Path.Combine(folder, "default"));
            Assert(CloudCapacity.Read(store).MaxParallel == 2, "legacy default changed");
            Assert(!File.Exists(Path.Combine(store.Root, CloudCapacity.Record)), "read created a preference");
        });
        check("cloud capacity persists three slots encrypted and preserves unrelated records", () => {
            var store = new RelayStore(Path.Combine(folder, "persistence"));
            store.WriteRecord("unrelated.dpapi", new { Keep = "fixture-unrelated" });
            byte[] unrelated = File.ReadAllBytes(Path.Combine(store.Root, "unrelated.dpapi"));
            CloudCapacity.Save(store, new CloudCapacityPreferences { MaxParallel = 3 });
            Assert(CloudCapacity.Read(new RelayStore(store.Root)).MaxParallel == 3, "restart lost capacity");
            Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(store.Root, CloudCapacity.Record))).Contains("MaxParallel"), "plaintext preference");
            Assert(File.ReadAllBytes(Path.Combine(store.Root, "unrelated.dpapi")).SequenceEqual(unrelated), "unrelated record changed");
        });
        check("cloud capacity accepts bounds one and eight and preserves an omitted legacy field", () => {
            var store = new RelayStore(Path.Combine(folder, "bounds"));
            foreach (int value in new[] { 1, 8 }) {
                CloudCapacity.Save(store, new CloudCapacityPreferences { MaxParallel = value });
                Assert(CloudCapacity.Read(store).MaxParallel == value, "valid boundary rejected");
            }
            store.WriteRecord(CloudCapacity.Record, new { });
            Assert(CloudCapacity.Read(store).MaxParallel == 2, "omitted field lost legacy default");
        });
        check("cloud capacity rejects invalid saves before replacing a valid preference", () => {
            var store = new RelayStore(Path.Combine(folder, "invalid"));
            CloudCapacity.Save(store, new CloudCapacityPreferences { MaxParallel = 4 });
            byte[] before = File.ReadAllBytes(Path.Combine(store.Root, CloudCapacity.Record));
            foreach (int value in new[] { -1, 0, 9, Int32.MaxValue })
                Reject(() => CloudCapacity.Save(store, new CloudCapacityPreferences { MaxParallel = value }));
            Reject(() => CloudCapacity.Save(store, null));
            Assert(File.ReadAllBytes(Path.Combine(store.Root, CloudCapacity.Record)).SequenceEqual(before), "invalid save replaced valid preference");
        });
        check("cloud capacity invalid persisted values never widen dispatch", () => {
            var store = new RelayStore(Path.Combine(folder, "invalid-read"));
            store.WriteRecord(CloudCapacity.Record, new CloudCapacityPreferences { MaxParallel = 99 });
            byte[] before = File.ReadAllBytes(Path.Combine(store.Root, CloudCapacity.Record));
            Reject(() => CloudCapacity.Read(store));
            Assert(File.ReadAllBytes(Path.Combine(store.Root, CloudCapacity.Record)).SequenceEqual(before), "invalid stored preference was silently rewritten");
        });
        check("cloud capacity compatibility default remains two active accounts", () => {
            var first = Job("a", "account-a"); var second = Job("b", "account-b"); var next = Job("c", "account-c", "queued");
            Assert(!CloudCapacity.CanStart(next, new[] { first, second, next }, CloudCapacity.DefaultMaximum), "default admitted a third job");
            first.State = "completed";
            Assert(CloudCapacity.CanStart(next, new[] { first, second, next }, CloudCapacity.DefaultMaximum), "completed job did not release default capacity");
        });
        check("cloud capacity three admits a third developer but preserves one mission per account", () => {
            var first = Job("a", "account-a"); var second = Job("b", "account-b"); var next = Job("c", "account-c", "queued");
            Assert(CloudCapacity.CanStart(next, new[] { first, second, next }, 3), "third developer was not admitted");
            next.AccountKey = first.AccountKey;
            Assert(!CloudCapacity.CanStart(next, new[] { first, second, next }, 3), "another instance of the same account bypassed its single slot");
            next.AccountKey = "account-c"; next.State = "running";
            var fourth = Job("d", "account-d", "queued");
            Assert(!CloudCapacity.CanStart(fourth, new[] { first, second, next, fourth }, 3), "three-slot limit admitted a fourth developer");
        });
        check("cloud capacity reduction waits without cancelling current work", () => {
            var active = Enumerable.Range(0, 3).Select(index => Job("active-" + index, "account-" + index)).ToArray();
            var queued = Job("queued", "account-new", "queued"); var jobs = active.Concat(new[] { queued }).ToArray();
            string before = Json.Write(jobs);
            var store = new RelayStore(Path.Combine(folder, "reduction"));
            CloudCapacity.Save(store, new CloudCapacityPreferences { MaxParallel = 1 });
            Assert(active.All(job => CloudCapacity.CanStart(job, jobs, CloudCapacity.Read(store).MaxParallel)), "lowering capacity stopped active work");
            Assert(!CloudCapacity.CanStart(queued, jobs, 1) && Json.Write(jobs) == before, "lowering capacity cancelled or changed existing jobs");
            active[0].State = "completed"; active[1].State = "completed";
            Assert(!CloudCapacity.CanStart(queued, jobs, 1), "last active slot admitted another job");
            active[2].State = "completed";
            Assert(CloudCapacity.CanStart(queued, jobs, 1), "completed work failed to release reduced capacity");
        });
        check("cloud capacity retains slots for native intervention and uncertain dispatched chats", () => {
            var waiting = Job("approval", "account-a", "attention"); var uncertain = Job("uncertain", "account-b", "uncertain"); var next = Job("next", "account-c", "queued");
            Assert(!CloudCapacity.CanStart(next, new[] { waiting, uncertain, next }, 2), "potentially active native work released capacity");
            Assert(CloudCapacity.CanStart(next, new[] { waiting, uncertain, next }, 3), "one remaining third slot was lost");
            waiting.ThreadId = null; uncertain.ThreadId = null;
            Assert(CloudCapacity.CanStart(next, new[] { waiting, uncertain, next }, 1), "uncreated chats invented execution slots");
        });
        check("cloud capacity future eight-account team remains bounded", () => {
            var active = Enumerable.Range(0, 7).Select(index => Job("active-" + index, "account-" + index)).ToArray();
            var next = Job("eighth", "account-eighth", "queued");
            Assert(CloudCapacity.CanStart(next, active.Concat(new[] { next }), 8), "eighth account could not start");
            next.State = "running";
            Assert(!CloudCapacity.CanStart(Job("ninth", "account-ninth", "queued"), active.Concat(new[] { next }), 8), "future team exceeded configured eight slots");
        });
        check("cloud capacity follow-up inherited chat is not an active inference before its prompt", () => {
            var follow = Job("follow", "account-a", "attention"); follow.Kind = "follow-up";
            var queued = Job("queued", "account-b", "queued");
            foreach (string state in new[] { "queued", "creating", "prepared", "attention" }) {
                follow.State = state;
                Assert(!CloudCapacity.ConsumesSlot(follow) && CloudCapacity.CanStart(queued, new[] { follow, queued }, 1), "inherited completed chat consumed a new inference slot in " + state);
                Assert(CloudCapacity.NeedsAdmission(follow) && !CloudCapacity.CanStart(follow, new[] { follow, Job("busy", "account-other") }, 1), "pre-prompt follow-up bypassed capacity in " + state);
            }
        });
        check("cloud capacity follow-up reserves capacity once StartTurn may have been sent", () => {
            var follow = Job("follow", "account-a", "sending"); follow.Kind = "follow-up";
            var queued = Job("queued", "account-b", "queued");
            foreach (string state in new[] { "sending", "uncertain", "running" }) {
                follow.State = state;
                Assert(CloudCapacity.ConsumesSlot(follow) && !CloudCapacity.CanStart(queued, new[] { follow, queued }, 1), "potentially sent follow-up released its slot in " + state);
            }
            follow.State = "attention"; follow.TurnId = "native-follow-up-turn";
            Assert(CloudCapacity.ConsumesSlot(follow) && !CloudCapacity.CanStart(queued, new[] { follow, queued }, 1), "native intervention on admitted follow-up released capacity");
            follow.State = "completed";
            Assert(!CloudCapacity.ConsumesSlot(follow) && CloudCapacity.CanStart(queued, new[] { follow, queued }, 1), "completed follow-up failed to release capacity");
        });
    }
}
