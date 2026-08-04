using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Collections.Generic;

namespace CodexProSafeDiagnosticHelper
{
    internal static class MaintenanceFilesystemSelfTest
    {
        internal static int Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "codexpro-maintenance-fs-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(Path.Combine(root, "nested"));
                Directory.CreateDirectory(Path.Combine(root, "race"));
                File.WriteAllBytes(Path.Combine(root, "alpha.txt"), Encoding.UTF8.GetBytes("alpha\r\nbeta\n"));
                File.WriteAllBytes(Path.Combine(root, "nested", "binary.dat"), new byte[] { 65, 0, 66 });
                File.WriteAllText(Path.Combine(root, "race", "nested.txt"), "nested");
                File.WriteAllText(Path.Combine(root, "changing.txt"), new string('x', 70000));
                File.WriteAllText(Path.Combine(root, "swap-read.txt"), new string('s', 70000));
                using (MaintenanceFilesystemProvider provider = new MaintenanceFilesystemProvider())
                {
                    if (provider.Bind(root) != "ok" || provider.Bind(root) != "already_bound") return 11;
                    MaintenanceWalkResponse first = provider.Walk(DefaultWalk());
                    if (first.status != "ok") return 12;
                    if (!first.complete) return 22;
                    if (first.entries.Count != 7) return 23;
                    string[] paths = first.entries.Select(item => item.relativePath).ToArray();
                    string[] expected = new[] { "alpha.txt", "nested", "nested/binary.dat" };
                    if (!expected.All(path => paths.Contains(path))) return 13;
                    MaintenanceEntry text = first.entries.Single(item => item.relativePath == "alpha.txt");
                    MaintenanceEntry binary = first.entries.Single(item => item.relativePath == "nested/binary.dat");
                    MaintenanceFileResponse read = provider.ReadTextFile(text.entryId, MaintenanceFilesystemProvider.MaximumTextBytes);
                    if (read.status != "ok" || Encoding.UTF8.GetString(Convert.FromBase64String(read.contentBase64)) != "alpha\r\nbeta\n") return 14;
                    MaintenanceFileResponse hash = provider.HashFile(text.entryId, MaintenanceFilesystemProvider.MaximumHashBytes);
                    if (hash.status != "ok" || hash.sha256 != read.sha256 || hash.contentBase64 != null) return 15;
                    if (provider.ReadTextFile(binary.entryId, MaintenanceFilesystemProvider.MaximumTextBytes).status != "not_text") return 16;
                    if (provider.ReadTextFile(text.entryId, 1).status != "too_large") return 24;
                    MaintenanceWalkResponse partial = provider.Walk(new MaintenanceWalkRequest
                    {
                        maxDepth = 64, maxEntries = 1, maxObservedBytes = MaintenanceFilesystemProvider.MaximumObservedBytes,
                        maxResponseBytes = MaintenanceFilesystemProvider.MaximumResponseBytes, maxDurationMs = MaintenanceFilesystemProvider.MaximumDurationMs
                    });
                    if (partial.status != "budget_exhausted" || partial.complete || partial.limitation != "entries" || partial.entries.Count != 0) return 17;
                    if (provider.HashFile(text.entryId, MaintenanceFilesystemProvider.MaximumHashBytes).status != "invalid_entry") return 18;

                    MaintenanceWalkResponse validAgain = provider.Walk(DefaultWalk());
                    MaintenanceEntry currentText = validAgain.entries.Single(item => item.relativePath == "alpha.txt");
                    MaintenanceWalkRequest invalidWalk = DefaultWalk();
                    invalidWalk.maxDepth = 0;
                    if (provider.Walk(invalidWalk).status != "invalid_request" ||
                        provider.HashFile(currentText.entryId, MaintenanceFilesystemProvider.MaximumHashBytes).status != "invalid_entry") return 25;

                    string race = Path.Combine(root, "race");
                    string raceBackup = Path.Combine(root, "race-backup");
                    MaintenanceFilesystemTestHooks.BeforeEntryOpen = delegate(string relative)
                    {
                        if (relative != "race") return;
                        MaintenanceFilesystemTestHooks.BeforeEntryOpen = null;
                        Directory.Move(race, raceBackup);
                        Directory.Move(raceBackup, race);
                    };
                    if (provider.Walk(DefaultWalk()).status != "unavailable") return 26;

                    MaintenanceWalkResponse ancestorSnapshot = provider.Walk(DefaultWalk());
                    MaintenanceEntry nested = ancestorSnapshot.entries.Single(item => item.relativePath == "race/nested.txt");
                    MaintenanceFilesystemTestHooks.BeforeAncestorOpen = delegate(string relative)
                    {
                        if (relative != "race") return;
                        MaintenanceFilesystemTestHooks.BeforeAncestorOpen = null;
                        Directory.Move(race, raceBackup);
                        Directory.CreateDirectory(race);
                        File.WriteAllText(Path.Combine(race, "nested.txt"), "decoy");
                    };
                    string ancestorStatus = provider.ReadTextFile(nested.entryId, 1024).status;
                    Directory.Delete(race, true);
                    Directory.Move(raceBackup, race);
                    if (ancestorStatus != "changed") return 27;

                    MaintenanceWalkResponse changingSnapshot = provider.Walk(DefaultWalk());
                    MaintenanceEntry changing = changingSnapshot.entries.Single(item => item.relativePath == "changing.txt");
                    MaintenanceFilesystemTestHooks.AfterReadChunk = delegate(string ignored)
                    {
                        MaintenanceFilesystemTestHooks.AfterReadChunk = null;
                        File.AppendAllText(Path.Combine(root, "changing.txt"), "changed");
                    };
                    if (provider.ReadTextFile(changing.entryId, MaintenanceFilesystemProvider.MaximumTextBytes).status != "changed") return 28;

                    MaintenanceWalkResponse swapSnapshot = provider.Walk(DefaultWalk());
                    MaintenanceEntry swapRead = swapSnapshot.entries.Single(item => item.relativePath == "swap-read.txt");
                    string swapPath = Path.Combine(root, "swap-read.txt");
                    string swapBackup = Path.Combine(root, "swap-read.backup");
                    MaintenanceFilesystemTestHooks.AfterReadChunk = delegate(string ignored)
                    {
                        MaintenanceFilesystemTestHooks.AfterReadChunk = null;
                        File.Move(swapPath, swapBackup);
                        File.Move(swapBackup, swapPath);
                    };
                    if (provider.ReadTextFile(swapRead.entryId, MaintenanceFilesystemProvider.MaximumTextBytes).status != "changed") return 33;

                    MaintenanceFilesystemTestHooks.AfterEnumeration = delegate
                    {
                        MaintenanceFilesystemTestHooks.AfterEnumeration = null;
                        Thread.Sleep(5);
                    };
                    MaintenanceWalkRequest duration = DefaultWalk();
                    duration.maxDurationMs = 1;
                    MaintenanceWalkResponse durationResult = provider.Walk(duration);
                    if (durationResult.status != "budget_exhausted" || durationResult.complete || durationResult.limitation != "duration") return 29;

                    MaintenanceWalkResponse estimate = new MaintenanceWalkResponse
                    {
                        protocol = MaintenanceProtocolServer.Protocol, operation = "walk", status = "ok", complete = true,
                        limitation = "none", entries = new List<MaintenanceEntry>()
                    };
                    string longPath = new string('p', 1000);
                    for (int index = 0; index < 4096; index++)
                        estimate.entries.Add(new MaintenanceEntry { entryId = "s00000001e" + index.ToString("x8"), relativePath = longPath, kind = "file", modifiedUtc = "2000-01-01T00:00:00.0000000Z", attributes = "none" });
                    int estimateBytes = MaintenanceProtocolServer.EstimateWalkResponseBytes(estimate,
                        new MaintenanceEntry { entryId = "s00000001effffffff", relativePath = longPath, kind = "file", modifiedUtc = "2000-01-01T00:00:00.0000000Z", attributes = "none" });
                    if (estimateBytes <= MaintenanceFilesystemProvider.MaximumResponseBytes) return 30;
                }
                string ordinaryFile = Path.Combine(root, "ordinary-file.txt");
                File.WriteAllText(ordinaryFile, "x");
                string[] rejectedRoots = new[]
                {
                    "relative", "\\\\server\\share", "\\\\.\\pipe\\fixture", "\\\\?\\C:\\fixture",
                    Path.Combine(root, "missing"), ordinaryFile
                };
                foreach (string rejectedRoot in rejectedRoots)
                {
                    using (MaintenanceFilesystemProvider provider = new MaintenanceFilesystemProvider())
                        if (provider.Bind(rejectedRoot) == "ok") return 31;
                }
                foreach (string invalidName in new[] { String.Empty, ".", "..", "a/b", "a\\b", "a:b", "a\0b", new string('x', 256) })
                {
                    bool rejected = false;
                    try { MaintenanceFilesystemProvider.ValidateBasename(invalidName); }
                    catch { rejected = true; }
                    if (!rejected) return 32;
                }
                return 0;
            }
            catch { return 20; }
            finally
            {
                MaintenanceFilesystemTestHooks.Reset();
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        private static MaintenanceWalkRequest DefaultWalk()
        {
            return new MaintenanceWalkRequest
            {
                maxDepth = MaintenanceFilesystemProvider.MaximumDepth,
                maxEntries = MaintenanceFilesystemProvider.MaximumEntries,
                maxObservedBytes = MaintenanceFilesystemProvider.MaximumObservedBytes,
                maxResponseBytes = MaintenanceFilesystemProvider.MaximumResponseBytes,
                maxDurationMs = MaintenanceFilesystemProvider.MaximumDurationMs
            };
        }
    }
}
