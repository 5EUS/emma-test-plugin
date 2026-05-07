using System.Text.Json.Serialization;
using EMMA.Plugin.Common;

namespace EMMA.TestPlugin.WASM;

[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(HandshakeResponse))]
[JsonSerializable(typeof(CapabilityItem[]))]
[JsonSerializable(typeof(MetadataItem))]
[JsonSerializable(typeof(IReadOnlyList<MetadataItem>))]
[JsonSerializable(typeof(List<MetadataItem>))]
[JsonSerializable(typeof(SearchItem[]))]
[JsonSerializable(typeof(ChapterItem[]))]
[JsonSerializable(typeof(WasmChapterOperationItem[]))]
[JsonSerializable(typeof(PageItem))]
[JsonSerializable(typeof(PageItem[]))]
[JsonSerializable(typeof(OperationResult))]
[JsonSerializable(typeof(BenchmarkResult))]
[JsonSerializable(typeof(NetworkBenchmarkResult))]
internal sealed partial class WasmJsonContext : JsonSerializerContext
{
}

internal sealed record WasmChapterOperationItem(
    string id,
    int number,
    string title,
    string[] uploaderGroups);
