using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Creezio.Switcher.Desktop
{
    internal static class CloudQueueSourceLabels
    {
        internal const int MaximumIndexBytes = 2 * 1024 * 1024;

        // The index is metadata only. Read its bounded tail once per home;
        // appending or rotating it must not prevent choosing an orchestrator.
        internal static IDictionary<string, string> ReadTitles(string home)
        {
            var titles = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using (var stream = new FileStream(Path.Combine(home, "session_index.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long end = stream.Length, start = Math.Max(0, end - MaximumIndexBytes); stream.Position = start;
                    var bytes = new byte[(int)(end - start)]; int count = 0;
                    while (count < bytes.Length) { int read = stream.Read(bytes, count, bytes.Length - count); if (read == 0) break; count += read; }
                    using (var reader = new StringReader(Encoding.UTF8.GetString(bytes, 0, count)))
                    {
                        if (start > 0) reader.ReadLine(); // A tail can start inside a UTF-8 record.
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            try
                            {
                                var entry = Json.Read<object>(line); string id = Json.Str(Json.Get(entry, "id")), title = Json.Str(Json.Get(entry, "thread_name"));
                                if (!String.IsNullOrEmpty(id) && !String.IsNullOrWhiteSpace(title)) titles[id] = title;
                            }
                            catch (ArgumentException) { }
                            catch (InvalidOperationException) { }
                            catch (System.Text.Json.JsonException) { }
                        }
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            return titles;
        }

        internal static CloudQueueSourceChoice[] Choices(IEnumerable<RelayChannel> channels, IEnumerable<CloudJob> jobs, Func<string, IDictionary<string, string>> readTitles = null)
        {
            readTitles = readTitles ?? ReadTitles;
            var result = new List<CloudQueueSourceChoice>(); var known = jobs.ToArray();
            var byHome = new Dictionary<string, IDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var channel in channels.Where(value => value.Enabled))
            {
                string home = Path.GetFullPath(channel.Home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!byHome.TryGetValue(home, out var titles)) { titles = readTitles(channel.Home); byHome.Add(home, titles); }
                // An index title never creates another selectable source. Keep
                // only registered anchors and chats already known as job sources.
                var threads = known.Where(job => job.SourceChannel == channel.Id && !String.IsNullOrEmpty(job.SourceThread)).Select(job => job.SourceThread).Concat(new[] { channel.AnchorThreadId }).Where(value => !String.IsNullOrEmpty(value)).Distinct();
                foreach (var thread in threads)
                {
                    string shortId = thread.Substring(0, Math.Min(18, thread.Length));
                    string caption = titles != null && titles.TryGetValue(thread, out string title) ? title + " · " + shortId : "Chat " + shortId;
                    result.Add(new CloudQueueSourceChoice { Label = channel.Name + " · " + channel.Email + " · " + caption, Source = new RelaySession { Channel = channel.Id, Thread = thread, Home = channel.Home, Account = channel.AccountKey } });
                }
            }
            return result.ToArray();
        }
    }
}
