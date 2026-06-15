namespace AOI_Monitor.Data;

public sealed record ImportedImage(
    long Id,
    string OriginalPath,
    string VaultPath,
    string FileName,
    string BoardModel,
    string LotId,
    string ViewType,
    DateTime ImportedAt,
    string FileHash);
