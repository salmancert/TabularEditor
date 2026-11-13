using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TabularEditor.Modern
{
    /// <summary>
    /// Reads Power BI report definitions (.pbix / .pbit files, or .pbip / PBIR report folders) and
    /// collects every model field the report refers to in visuals, filters and formatting.
    /// </summary>
    internal class ReportScanner
    {
        /// <summary>Fields referenced as "table\nfield" (lower case).</summary>
        public readonly HashSet<string> Fields = new HashSet<string>();
        /// <summary>Hierarchy levels referenced as "table\nhierarchy\nlevel" (lower case).</summary>
        public readonly HashSet<string> Levels = new HashSet<string>();
        public int DocumentsRead { get; private set; }

        public static string Key(string table, string field) => (table + "\n" + field).ToLowerInvariant();
        public static string Key(string table, string hierarchy, string level) => (table + "\n" + hierarchy + "\n" + level).ToLowerInvariant();

        /// <summary>Scans a report file or folder. Throws when nothing readable is found.</summary>
        public void Scan(string path)
        {
            int before = DocumentsRead;
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (Directory.Exists(path)) ScanFolder(path);
            else if (extension == ".pbix" || extension == ".pbit") ScanArchive(path);
            else if (extension == ".pbip")
            {
                // A .pbip sits next to one or more "<name>.Report" folders.
                var folder = Path.GetDirectoryName(path);
                var reports = Directory.GetDirectories(folder, "*.Report");
                if (reports.Length == 0) ScanFolder(folder);
                foreach (var report in reports) ScanFolder(report);
            }
            else ScanFolder(Path.GetDirectoryName(path));   // a .pbir or .json inside a report folder

            if (DocumentsRead == before) throw new InvalidDataException("No report definition was found in " + path);
        }

        private void ScanArchive(string file)
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    bool legacy = name.Equals("Report/Layout", StringComparison.OrdinalIgnoreCase);
                    bool pbir = name.StartsWith("Report/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
                    if (!legacy && !pbir) continue;
                    using (var entryStream = entry.Open())
                    using (var memory = new MemoryStream())
                    {
                        entryStream.CopyTo(memory);
                        var bytes = memory.ToArray();
                        ScanText(legacy ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes));
                    }
                }
            }
        }

        private void ScanFolder(string folder)
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories))
            {
                // Skip the semantic model definition: it names every field and would mark all as used.
                if (file.IndexOf(".SemanticModel", StringComparison.OrdinalIgnoreCase) >= 0
                    || file.IndexOf(".Dataset", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                try { ScanText(File.ReadAllText(file)); } catch (IOException) { }
            }
        }

        private void ScanText(string text)
        {
            text = text.TrimStart('﻿', ' ', '\r', '\n', '\t');
            if (text.Length == 0 || (text[0] != '{' && text[0] != '[')) return;
            JToken root;
            try { root = JToken.Parse(text); } catch (JsonException) { return; }
            DocumentsRead++;
            Walk(root, null, 0);
        }

        private void Walk(JToken token, Dictionary<string, string> aliases, int depth)
        {
            if (depth > 200) return;
            if (token is JObject obj)
            {
                // A query block declares its table aliases in "From".
                if (obj["From"] is JArray from)
                {
                    var scoped = aliases == null ? new Dictionary<string, string>() : new Dictionary<string, string>(aliases);
                    foreach (var source in from.OfType<JObject>())
                    {
                        var name = (source["Name"] as JValue)?.Value as string;
                        var entity = (source["Entity"] as JValue)?.Value as string;
                        if (name != null && entity != null) scoped[name] = entity;
                    }
                    aliases = scoped;
                }

                var property = (obj["Property"] as JValue)?.Value as string;
                if (property != null && obj["Expression"] is JObject expression)
                {
                    var entity = ResolveEntity(expression, aliases);
                    if (entity != null) Fields.Add(Key(entity, property));
                }

                var level = (obj["Level"] as JValue)?.Value as string;
                if (level != null && obj["Expression"] is JObject levelExpression && levelExpression["Hierarchy"] is JObject hierarchy)
                {
                    var hierarchyName = (hierarchy["Hierarchy"] as JValue)?.Value as string;
                    var entity = hierarchy["Expression"] is JObject inner ? ResolveEntity(inner, aliases) : null;
                    if (entity != null && hierarchyName != null) Levels.Add(Key(entity, hierarchyName, level));
                }

                foreach (var child in obj.Properties()) Walk(child.Value, aliases, depth + 1);
            }
            else if (token is JArray array)
            {
                foreach (var child in array) Walk(child, aliases, depth + 1);
            }
            else if (token is JValue value && value.Value is string text && text.Length > 1)
            {
                // The legacy layout stores visual configuration, queries and filters as JSON inside strings.
                var trimmed = text.TrimStart();
                if (trimmed.Length > 1 && (trimmed[0] == '{' || trimmed[0] == '['))
                {
                    try { Walk(JToken.Parse(trimmed), aliases, depth + 1); } catch (JsonException) { }
                }
            }
        }

        private static string ResolveEntity(JObject expression, Dictionary<string, string> aliases)
        {
            var sourceRef = expression["SourceRef"] as JObject;
            if (sourceRef == null) return null;
            var entity = (sourceRef["Entity"] as JValue)?.Value as string;
            if (entity != null) return entity;
            var alias = (sourceRef["Source"] as JValue)?.Value as string;
            string resolved;
            return alias != null && aliases != null && aliases.TryGetValue(alias, out resolved) ? resolved : null;
        }
    }
}
