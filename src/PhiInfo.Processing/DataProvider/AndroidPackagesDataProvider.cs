using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PhiInfo.Core.Type;
using Shua.Zip;

namespace PhiInfo.Processing.DataProvider;

public class AndroidPackagesDataProvider(IEnumerable<ShuaZip> zips, Stream cldbStream) : IDataProvider
{
    private const string DataPrefix = "assets/bin/Data/";
    private const string RuntimePathPlaceholder = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}";
    private const string PlayerDataName = "data.unity3d";
    private bool _disposed;
    private bool _playerDataLoaded;
    private AssetBundleFile? _playerData;

    public Stream GetCldb()
    {
        var ms = new MemoryStream();
        cldbStream.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }

    public Stream GetGlobalGameManagers()
    {
        return GetDataFile("globalgamemanagers.assets");
    }

    public byte[] GetIl2CppBinary()
    {
        var (zip, entry) = FindEntryInAllZips("lib/arm64-v8a/libil2cpp.so");
        return zip.ReadFile(entry);
    }

    public byte[] GetGlobalMetadata()
    {
        if (TryFindEntryInAllZips("assets/bin/Data/Managed/Metadata/game.dat", out var zip, out var entry))
        {
            var data = zip.ReadFile(entry);
            return DecryptOldMetaData.Decrypt(data);
        }

        var (zip2, entry2) = FindEntryInAllZips("assets/bin/Data/Managed/Metadata/global-metadata.dat");
        return zip2.ReadFile(entry2);
    }

    public Stream GetDataFile(string name)
    {
        if (TryFindEntryInAllZips(DataPrefix + name, out var zip, out var entry))
            return EnsureSeekable(zip.OpenFileStream(entry));

        // 旧版本会把 level 等文件切成 <name>.splitN 分片
        var parts = FindSplitParts(name);
        if (parts.Count != 0)
            return ConcatParts(parts);

        // 4.0.1 起序列化文件(level、sharedassets 等)被打包进 data.unity3d
        if (TryOpenPlayerDataFile(name, out var playerDataStream))
            return playerDataStream;

        throw new FileNotFoundException($"Required Unity asset '{DataPrefix}{name}' missing from provided packages.");
    }

    private List<(int index, string name, ShuaZip zip)> FindSplitParts(string name)
    {
        var partPrefix = DataPrefix + name + ".split";
        var parts = new List<(int index, string name, ShuaZip zip)>();

        foreach (var item in zips)
        {
            foreach (var fileEntry in item.Eocd.FileEntries)
            {
                if (!fileEntry.Name.StartsWith(partPrefix, StringComparison.Ordinal))
                    continue;

                var suffix = fileEntry.Name[partPrefix.Length..];
                if (int.TryParse(suffix, out var index))
                    parts.Add((index, fileEntry.Name, item));
            }
        }

        parts.Sort((a, b) => a.index.CompareTo(b.index));
        return parts;
    }

    private static MemoryStream ConcatParts(IReadOnlyList<(int index, string name, ShuaZip zip)> parts)
    {
        MemoryStream data = new();

        foreach (var (_, partName, partZip) in parts)
        {
            var part = partZip.ReadFileByName(partName);
            data.Write(part, 0, part.Length);
        }

        data.Position = 0;
        return data;
    }

    public IReadOnlyList<string> GetDataFileNames()
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var zip in zips)
        {
            foreach (var entry in zip.Eocd.FileEntries)
            {
                if (!entry.Name.StartsWith(DataPrefix, StringComparison.Ordinal))
                    continue;

                var name = entry.Name[DataPrefix.Length..];

                // 跳过子目录与 .resource 资源流
                if (name.Contains('/') || name.EndsWith(".resource", StringComparison.Ordinal))
                    continue;

                var splitIndex = name.IndexOf(".split", StringComparison.Ordinal);
                if (splitIndex > 0)
                    name = name[..splitIndex];

                if (seen.Add(name))
                    names.Add(name);
            }
        }

        // 4.0.1 起序列化文件被打包进 data.unity3d
        if (PlayerData is { } playerData)
        {
            foreach (var name in playerData.GetAllFileNames())
            {
                if (seen.Add(name))
                    names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    ///     assets/bin/Data/data.unity3d,内部为 LZ4HC 分块压缩的 UnityFS。
    /// </summary>
    private AssetBundleFile? PlayerData
    {
        get
        {
            if (_playerDataLoaded) return _playerData;
            _playerDataLoaded = true;

            if (!TryFindEntryInAllZips(DataPrefix + PlayerDataName, out var zip, out var entry))
                return _playerData = null;

            var stream = EnsureSeekable(zip.OpenFileStream(entry));

            var bundle = new AssetBundleFile();
            bundle.Read(new AssetsFileReader(stream));

            if (!bundle.DataIsCompressed)
                return _playerData = bundle;

            var unpacked = BundleHelper.UnpackBundle(bundle);
            bundle.Close();
            stream.Dispose();

            return _playerData = unpacked;
        }
    }

    private bool TryOpenPlayerDataFile(string name, [NotNullWhen(true)] out Stream? stream)
    {
        stream = null;

        if (PlayerData is not { } playerData)
            return false;

        var index = playerData.GetFileIndex(name);
        if (index < 0)
            return false;

        playerData.GetFileRange(index, out var offset, out var size);

        stream = new SegmentStream(playerData.DataReader.BaseStream, offset, size);
        return true;
    }

    public Stream GetCatalog()
    {
        var (zip, entry) = FindEntryInAllZips("assets/aa/catalog.json");
        return EnsureSeekable(zip.OpenFileStream(entry));
    }

    public Stream GetBundle(string name)
    {
        var path = name.Replace(RuntimePathPlaceholder, "assets/aa");

        if (TryFindEntryInAllZips(path, out var zip, out var entry))
            return EnsureSeekable(zip.OpenFileStream(entry));

        // 4.0 起 catalog 中的 bundle 名为 <hash1>_<hash2>.bundle,包内实际存放的是 <hash2>.bundle
        var nameIndex = path.LastIndexOf('/') + 1;
        var hashIndex = path.IndexOf('_', nameIndex);

        if (hashIndex > nameIndex)
        {
            var stripped = string.Concat(path.AsSpan(0, nameIndex), path.AsSpan(hashIndex + 1));
            if (TryFindEntryInAllZips(stripped, out var strippedZip, out var strippedEntry))
                return EnsureSeekable(strippedZip.OpenFileStream(strippedEntry));
        }

        throw new FileNotFoundException($"Required Unity asset '{path}' missing from provided packages.");
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private bool TryFindEntryInAllZips(
        string fileName,
        [NotNullWhen(true)] out ShuaZip? zip,
        [NotNullWhen(true)] out FileEntry? entry)
    {
        foreach (var item in zips)
        {
            entry = item.TryFindEntry(fileName);
            if (entry is not null)
            {
                zip = item;
                return true;
            }
        }

        zip = null;
        entry = null;
        return false;
    }

    internal (ShuaZip, FileEntry) FindEntryInAllZips(string fileName)
    {
        if (TryFindEntryInAllZips(fileName, out var zip, out var entry))
            return (zip, entry);

        throw new FileNotFoundException($"Required Unity asset '{fileName}' missing from provided packages.");
    }

    private static Stream EnsureSeekable(Stream stream)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
            return stream;
        }

        var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;

        stream.Dispose();
        return ms;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _playerData?.Close();
            cldbStream.Dispose();
            foreach (var item in zips) item.Dispose();
        }

        _disposed = true;
    }
}
