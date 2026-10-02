using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsTools.NET;
using PhiInfo.Core.Type;
using Shua.UA.Core.Field;

namespace PhiInfo.Core;

public class InfoProvider : IDisposable
{
    private const uint SharedAssetsCollectionVersion = 155;

    private const string GameInformationScript = "GameInformation";
    private const string CollectionSceneScript = "SaturnOSControl";
    private const string CollectionDatabaseScript = "CollectionDatabase";
    private const string GetCollectionControlScript = "GetCollectionControl";
    private const string TipsProviderScript = "TipsProvider";

    private static readonly string[] RequiredScripts =
    {
        GameInformationScript,
        CollectionSceneScript,
        GetCollectionControlScript,
        TipsProviderScript
    };

    private readonly IInfoDataProvider _dataProvider;
    private readonly FieldProvider _fieldProvider;
    private readonly Lazy<Dictionary<string, ScriptEntry>> _scripts;
    private readonly Lazy<PhiVersion> _version;
    private bool _disposed;

    public InfoProvider(
        IInfoDataProvider dataProvider,
        FieldProvider fieldProvider)
    {
        _dataProvider = dataProvider;
        _fieldProvider = fieldProvider;

        _scripts = new Lazy<Dictionary<string, ScriptEntry>>(
            FindScripts);

        _version = new Lazy<PhiVersion>(GetPhiVersion);
    }

    private static AssetsFile ReadAssetsFile(Stream stream)
    {
        var file = new AssetsFile();
        file.Read(new AssetsFileReader(stream));
        return file;
    }

    private Dictionary<string, ScriptEntry> FindScripts()
    {
        var result = new Dictionary<string, ScriptEntry>(
            StringComparer.Ordinal);

        var required = new HashSet<string>(
            RequiredScripts,
            StringComparer.Ordinal);

        // CollectionDatabase 是 4.0 (code 155) 起才有的收藏品数据库
        if (_version.Value.code >= SharedAssetsCollectionVersion)
            required.Add(CollectionDatabaseScript);

        foreach (var name in _dataProvider.GetDataFileNames())
        {
            AssetsFile file;

            try
            {
                file = ReadAssetsFile(_dataProvider.GetDataFile(name));
            }
            catch (Exception)
            {
                continue;
            }

            var found = false;

            foreach (var scriptName in required)
            {
                if (result.ContainsKey(scriptName))
                    continue;

                try
                {
                    var behaviour = _fieldProvider.TryFindMonoBehaviour(
                        file,
                        scriptName);

                    if (behaviour is null)
                        continue;

                    result.Add(
                        scriptName,
                        new ScriptEntry(file, behaviour));

                    found = true;
                }
                catch (Exception)
                {
                    // 当前文件无法解析该脚本时继续寻找。
                }
            }

            if (result.Count == required.Count)
                break;

            if (!found)
                file.Close();
        }

        var missing = required
            .Where(script => !result.ContainsKey(script))
            .ToList();

        if (missing.Count != 0)
        {
            foreach (var file in result.Values
                         .Select(x => x.File)
                         .Distinct())
            {
                file.Close();
            }

            throw new InvalidOperationException(
                $"Cannot find MonoBehaviours: {string.Join(", ", missing)}");
        }

        return result;
    }

    private AssetTypeValueField GetScript(string scriptName)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(InfoProvider));

        return _scripts.Value[scriptName].Behaviour;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        _disposed = true;

        if (disposing && _scripts.IsValueCreated)
        {
            foreach (var file in _scripts.Value
                         .Values
                         .Select(x => x.File)
                         .Distinct())
            {
                file.Close();
            }
        }
    }

    private static Dictionary<Language, string> ExtractMultiLang(
        AssetTypeValueField field,
        Func<string, string>? hook = null)
    {
        var result = new Dictionary<Language, string>();

        foreach (var child in field.Children)
        {
            if (child.FieldName == "code")
                continue;

            var language = Extensions.FromString(child.FieldName);
            var value = child.AsString;

            if (hook != null)
                value = hook(value);

            result[language] = value;
        }

        return result;
    }

    public List<SongInfo> ExtractSongs()
    {
        var gameInfo = GetScript(GameInformationScript);

        var songs = gameInfo["song"]
            .Children
            .SelectMany(songGroup => songGroup["Array"].Children)
            .Select(song =>
            {
                var levels = song["levels"]["Array"].Children;
                var charters = song["charter"]["Array"].Children;
                var diffs = song["difficulty"]["Array"].Children;

                var levelDict = diffs
                    .Select((diffNode, i) => new
                    {
                        Diff = diffNode.AsDouble,
                        Level = levels[i].AsString,
                        Charter = charters[i].AsString
                    })
                    .Where(x => x.Diff != 0)
                    .ToDictionary(
                        x => x.Level,
                        x => new SongLevel(
                            x.Charter,
                            Math.Round(x.Diff, 1)));

                return levelDict.Count == 0
                    ? null
                    : new SongInfo(
                        song["songsId"].AsString,
                        song["songsKey"].AsString,
                        song["songsName"].AsString,
                        song["composer"].AsString,
                        song["illustrator"].AsString,
                        Math.Round(song["previewTime"].AsDouble, 2),
                        Math.Round(song["previewEndTime"].AsDouble, 2),
                        levelDict);
            })
            .Where(song => song != null)
            .ToList();

        return songs!;
    }

    public List<Folder> ExtractCollection()
    {
        if (_version.Value.code >= SharedAssetsCollectionVersion)
            return ExtractCollectionFromSharedAssets();

        var control = GetScript(CollectionSceneScript);

        return control["folders"]["Array"].Children
            .Select(folder =>
            {
                var files = folder["files"]["Array"].Children
                    .Select(ExtractFileItem)
                    .ToList();

                return new Folder(
                    ExtractMultiLang(folder["title"]),
                    ExtractMultiLang(folder["subTitle"]),
                    folder["cover"].AsString,
                    files);
            })
            .ToList();
    }

    private List<Folder> ExtractCollectionFromSharedAssets()
    {
        var database = GetScript(CollectionDatabaseScript);

        var items = database["items"]["Array"].Children
            .Select(item => new CollectionEntry(
                ExtractFileItem(item),
                Math.Abs(item["getSong"].AsInt)))
            .ToList();

        var control = GetScript(CollectionSceneScript);

        return control["folders"]["Array"].Children
            .Select(folder => new Folder(
                ExtractMultiLang(folder["title"]),
                ExtractMultiLang(folder["subTitle"]),
                folder["cover"].AsString,
                ExtractFolderFiles(folder, items)))
            .ToList();
    }

    private static List<FileItem> ExtractFolderFiles(
        AssetTypeValueField folder,
        List<CollectionEntry> items)
    {
        var start = folder["startIndex"].AsInt;
        var end = folder["endIndex"].AsInt;

        var excluded = folder["excludedFiles"]["Array"].Children
            .Select(range => (
                Start: range["start"].AsInt,
                End: range["end"].AsInt))
            .ToList();

        var files = items
            .Where(item =>
                item.Index >= start &&
                item.Index <= end &&
                !excluded.Any(range =>
                    item.Index >= range.Start &&
                    item.Index <= range.End))
            .Select(item => item.File)
            .ToList();

        foreach (var reference in folder["includedIsolatedFiles"]["Array"].Children)
        {
            var key = reference["key"].AsString;
            var subIndex = reference["subIndex"].AsInt;

            var item = items.FirstOrDefault(entry =>
                entry.File.key == key &&
                entry.File.sub_index == subIndex);

            if (item != null && !files.Contains(item.File))
                files.Add(item.File);
        }

        return files;
    }

    private static FileItem ExtractFileItem(AssetTypeValueField file)
    {
        return new FileItem(
            file["key"].AsString,
            file["subIndex"].AsInt,
            ExtractMultiLang(file["name"]),
            file["date"].AsString,
            ExtractMultiLang(file["supervisor"]),
            file["category"].AsString,
            ExtractMultiLang(
                file["content"],
                value => value.Replace("\\n", "\n")),
            ExtractMultiLang(file["properties"]));
    }

    private sealed record CollectionEntry(
        FileItem File,
        int Index);

    private sealed record ScriptEntry(
        AssetsFile File,
        AssetTypeValueField Behaviour);

    public List<Avatar> ExtractAvatars()
    {
        var control = GetScript(GetCollectionControlScript);

        return control["avatars"]["Array"].Children
            .Select(a => new Avatar(
                a["name"].AsString,
                a["addressableKey"].AsString))
            .ToList();
    }

    public Dictionary<Language, List<string>> ExtractTips()
    {
        var provider = GetScript(TipsProviderScript);

        var result = new Dictionary<Language, List<string>>();

        foreach (var entry in provider["tips"]["Array"].Children)
        {
            var language = Extensions.FromInt(
                entry["language"].AsInt);

            var tips = entry["tips"]["Array"].Children
                .Select(t => t.AsString)
                .ToList();

            result[language] = tips;
        }

        return result;
    }

    public List<ChapterInfo> ExtractChapters()
    {
        var gameInfo = GetScript(GameInformationScript);

        return gameInfo["chapters"]["Array"].Children
            .Select(chapter =>
            {
                var songInfo = chapter["songInfo"];

                var songs = songInfo["songs"]["Array"].Children
                    .Select(s => s["songsId"].AsString)
                    .ToList();

                return new ChapterInfo(
                    chapter["chapterCode"].AsString,
                    songInfo["banner"].AsString,
                    songs);
            })
            .ToList();
    }

    public PhiVersion GetPhiVersion()
    {
        var meta = _fieldProvider.GetMetadata();

        var assembly = meta.AssemblyDefinitions
                           .FirstOrDefault(a =>
                               a.AssemblyName.Name == "Assembly-CSharp")
                       ?? throw new InvalidDataException(
                           "Cannot find Assembly-CSharp.");

        var type = assembly.Image.Types?
                       .FirstOrDefault(t => t.FullName == "Constants")
                   ?? throw new InvalidDataException(
                       "Cannot find Constants class.");

        var codeField = type.Fields?
                            .FirstOrDefault(f => f.Name == "IntVersion")
                        ?? throw new InvalidDataException(
                            "Cannot find IntVersion field.");

        var codeDefaultValue =
            meta.GetFieldDefaultValue(codeField)?.Value
            ?? throw new InvalidDataException(
                "There is no default value for the IntVersion field.");

        var nameField = type.Fields?
                            .FirstOrDefault(f => f.Name == "Version")
                        ?? throw new InvalidDataException(
                            "Cannot find Version field.");

        var nameDefaultValue =
            meta.GetFieldDefaultValue(nameField)?.Value
            ?? throw new InvalidDataException(
                "There is no default value for the Version field.");

        if (codeDefaultValue is int intValue &&
            nameDefaultValue is string stringValue)
        {
            return new PhiVersion(
                (uint)intValue,
                stringValue);
        }

        throw new InvalidDataException(
            $"Invalid version type: " +
            $"{nameDefaultValue.GetType()} and " +
            $"{codeDefaultValue.GetType()}");
    }

    public AllInfo ExtractAllInfo()
    {
        return new AllInfo(
            GetPhiVersion(),
            ExtractSongs(),
            ExtractCollection(),
            ExtractAvatars(),
            ExtractTips(),
            ExtractChapters());
    }
}
