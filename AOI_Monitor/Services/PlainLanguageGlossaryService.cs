namespace AOI_Monitor.Services;

public sealed record PlainLanguageGlossaryEntry(string Key, string Term, string PlainEnglish, string KoreanFriendly);

public static class PlainLanguageGlossaryService
{
    private static readonly PlainLanguageGlossaryEntry[] Entries =
    [
        new("FalseCall", "False Call", "The system marked a good board as a defect.", "정상 보드를 불량으로 표시"),
        new("PossibleEscape", "Possible Escape", "The system may have missed a real defect.", "실제 불량을 놓쳤을 수 있음"),
        new("ROI", "ROI", "Inspection area on the board.", "보드 검사 영역"),
        new("MES", "MES", "Factory traceability system for production records and result upload.", "공장 추적/생산 이력 시스템"),
        new("ONNXModel", "ONNX Model", "AI model file used for inspection.", "검사에 사용하는 AI 모델 파일"),
        new("AcceptanceTest", "Acceptance Test", "Evidence check only. It is not final certification.", "증거 확인이며 최종 인증은 아님"),
    ];

    public static IReadOnlyList<PlainLanguageGlossaryEntry> All => Entries;

    public static PlainLanguageGlossaryEntry Get(string key)
        => Entries.First(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public static string Explain(string key)
    {
        var entry = Get(key);
        return UiPreferencesService.Load().Language == UiLanguage.Korean
            ? $"{entry.Term}: {entry.KoreanFriendly}"
            : $"{entry.Term}: {entry.PlainEnglish}";
    }

    public static string EvidenceMissing(string whatHappened, string whyItMatters, string missingEvidence)
        => $"What happened: {whatHappened} Why it matters: {whyItMatters} Missing evidence: {missingEvidence}";

    public static string AcceptanceBoundary(string subject)
        => $"{subject} acceptance is an evidence check only. It does not certify final production readiness or safety.";

    public static string SafeOperatorError(string operationName, string reportPath = "")
    {
        var suffix = string.IsNullOrWhiteSpace(reportPath)
            ? "A diagnostic report could not be written."
            : $"Diagnostic report: {reportPath}";
        return $"The {operationName} action failed safely. The app is still usable. {suffix}";
    }
}
