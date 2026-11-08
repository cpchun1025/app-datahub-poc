namespace DataHub.Api.Sources;

/// <summary>
/// 提供目前啟用嘅所有來源 adapter。預設實作係 mock catalog；測試或之後接真實來源時，可以替換呢個註冊。
/// </summary>
public interface ISourceCatalog
{
    IReadOnlyList<ISourceAdapter> Adapters { get; }
}
