using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AOI_Monitor.Data;

namespace AOI_Monitor.Services;

public enum UiLanguage
{
    English,
    Korean,
}

public enum UiFontPreset
{
    Compact,
    Standard,
    Large,
}

public enum UiTheme
{
    IndustrialDark,
    IndustrialLight,
}

public enum UiResolutionPreset
{
    FullHd1920x1080,
    Qhd2560x1440,
    Uhd3840x2160,
}

public sealed class UiPreferences
{
    public UiLanguage Language { get; set; } = UiLanguage.English;
    public UiFontPreset FontPreset { get; set; } = UiFontPreset.Standard;
    public UiTheme Theme { get; set; } = UiTheme.IndustrialDark;
    public UiResolutionPreset ResolutionPreset { get; set; } = UiResolutionPreset.FullHd1920x1080;
    public string ConsoleTitle { get; set; } = UiPreferenceDefaults.ConsoleTitle;
    public string StationDisplayName { get; set; } = UiPreferenceDefaults.StationDisplayName;
    public string StationSubtitle { get; set; } = UiPreferenceDefaults.StationSubtitle;
    public string AccentColor { get; set; } = UiPreferenceDefaults.AccentColor;
    public string BrandLogoPath { get; set; } = string.Empty;
}

public static class UiPreferenceDefaults
{
    public const string ConsoleTitle = "PCBA AOI REVIEW CONSOLE";
    public const string StationDisplayName = "AOI-LIB";
    public const string StationSubtitle = "local review console / prototype";
    public const string AccentColor = "#5CA0D3";
}

public static class UiPreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly DependencyProperty OriginalTextProperty =
        DependencyProperty.RegisterAttached("OriginalText", typeof(string), typeof(UiPreferencesService));
    private static readonly DependencyProperty OriginalContentProperty =
        DependencyProperty.RegisterAttached("OriginalContent", typeof(string), typeof(UiPreferencesService));
    private static readonly DependencyProperty OriginalHeaderProperty =
        DependencyProperty.RegisterAttached("OriginalHeader", typeof(string), typeof(UiPreferencesService));
    private static readonly DependencyProperty OriginalToolTipProperty =
        DependencyProperty.RegisterAttached("OriginalToolTip", typeof(string), typeof(UiPreferencesService));
    private static readonly Dictionary<string, string> KoreanText = new(StringComparer.Ordinal)
    {
        ["PCBA AOI REVIEW CONSOLE"] = "PCBA AOI 검사 콘솔",
        ["Demo Mode"] = "데모 모드",
        ["Pilot Mode"] = "파일럿 모드",
        ["Production Mode"] = "운영 모드",
        ["Profile "] = "프로필 ",
        ["User "] = "사용자 ",
        ["Engine "] = "엔진 ",
        ["File"] = "파일",
        ["Report Issue"] = "이슈 보고",
        ["Layout Stress"] = "레이아웃 점검",
        ["Support Bundle"] = "지원 번들",
        ["Session and admin access are available in the collapsed Access panel."] = "세션 및 관리자 기능은 접힌 접근 패널에서 사용할 수 있습니다.",
        ["Access / User Management"] = "접근 / 사용자 관리",
        ["Utilities / Access"] = "유틸리티 / 접근",
        ["AOI Workflow"] = "AOI 워크플로",
        ["Home"] = "홈",
        ["module map"] = "모듈 맵",
        ["Run Inspection"] = "검사 실행",
        ["board execution"] = "보드 실행",
        ["Review Defects"] = "결함 검토",
        ["evidence and disposition"] = "증빙 및 처분",
        ["Analyze Yield"] = "수율 분석",
        ["accuracy and false calls"] = "정확도 및 허위 검출",
        ["Recipe & Model"] = "레시피 / 모델",
        ["ROI, tolerances, lifecycle"] = "ROI, 공차, 수명주기",
        ["Export / Trace"] = "내보내기 / 추적",
        ["logs, reports, MES"] = "로그, 보고서, MES",
        ["System Health"] = "시스템 건강",
        ["hardware, quality, access"] = "하드웨어, 품질, 접근",
        ["Board & Images"] = "보드 / 이미지",
        ["library, folders, golden refs"] = "라이브러리, 폴더, 기준 이미지",
        ["Golden Compare"] = "기준 비교",
        ["template match, difference score"] = "템플릿 매칭, 차이 점수",
        ["Defect Review"] = "결함 검토",
        ["queue, evidence, disposition"] = "대기열, 증빙, 처분",
        ["Recipe Rules"] = "레시피 규칙",
        ["ROI, masks, tolerances"] = "ROI, 마스크, 공차",
        ["AI / Models"] = "AI / 모델",
        ["model checks, false calls"] = "모델 점검, 허위 검출",
        ["Yield Analytics"] = "수율 분석",
        ["SPC, Pareto, trends"] = "SPC, 파레토, 추세",
        ["Export & Trace"] = "내보내기 / 추적",
        ["CSV, PDF, audit, MES"] = "CSV, PDF, 감사, MES",
        ["2D transform, Stage 2 prep"] = "2D 변환, 2단계 준비",
        ["3D Profile"] = "3D 프로파일",
        ["height data, acceptance"] = "높이 데이터, 승인",
        ["Hardware Readiness"] = "하드웨어 준비성",
        ["camera, lighting, robot gates"] = "카메라, 조명, 로봇 게이트",
        ["System Settings"] = "시스템 설정",
        ["display, storage, security"] = "화면, 저장소, 보안",
        ["Database"] = "데이터베이스",
        ["Image Vault"] = "이미지 보관소",
        ["Engine"] = "엔진",
        ["Camera"] = "카메라",
        ["Lighting"] = "조명",
        ["Robot"] = "로봇",
        ["MES / Trace"] = "MES / 추적",
        ["E-Stop"] = "비상정지",
        ["Connected"] = "연결됨",
        ["Available"] = "사용 가능",
        ["Ready"] = "준비됨",
        ["Simulated"] = "시뮬레이션",
        ["Error"] = "오류",
        ["Not Connected"] = "연결 안 됨",
        ["Not Available"] = "사용 불가",
        ["MAIN INSPECTION / REVIEW WORKFLOW"] = "메인 검사 / 검토 워크플로",
        ["Main Inspection"] = "메인 검사",
        ["Recipe Editor"] = "레시피 편집",
        ["AI Model Test"] = "AI 모델 테스트",
        ["Log & Export"] = "로그 / 내보내기",
        ["Pilot Wizard"] = "파일럿 마법사",
        ["3D Profile Viewer"] = "3D 프로파일 뷰어",
        ["Calibration"] = "보정",
        ["Settings / Guide"] = "설정 / 가이드",
        ["Station AOI-LIB-01"] = "스테이션 AOI-LIB-01",
        ["Camera: Stage 1 folder source"] = "카메라: 1단계 폴더 소스",
        ["Lighting: simulated boundary"] = "조명: 시뮬레이션 경계",
        ["DB: SQLite local cache"] = "DB: SQLite 로컬 캐시",
        ["Robot/MES: not production-connected"] = "로봇/MES: 운영 연결 아님",
        ["Product"] = "제품",
        ["Recipe"] = "레시피",
        ["Inspection Basis"] = "검사 기준",
        ["Image validation PoC"] = "이미지 검증 PoC",
        ["Primary Modules"] = "주요 모듈",
        ["Focused Workflow Windows"] = "집중 워크플로 창",
        ["AOI Inspection Pipeline"] = "AOI 검사 파이프라인",
        ["Input"] = "입력",
        ["PNG/JPG, AOI files, golden images"] = "PNG/JPG, AOI 파일, 기준 이미지",
        ["Register"] = "정합",
        ["Fiducials, rotation, X/Y offset"] = "피듀셜, 회전, X/Y 오프셋",
        ["Component, solder, polarity, surface"] = "부품, 솔더, 극성, 표면",
        ["Evidence, coordinates, IPC class, MES"] = "증빙, 좌표, IPC 등급, MES",
        ["Boards inspected"] = "검사 보드",
        ["First-pass yield"] = "초회 통과율",
        ["Pending review"] = "검토 대기",
        ["Critical defects"] = "치명 결함",
        ["Capabilities represented in the redesigned workflow"] = "재설계 워크플로에 반영된 기능",
        ["Capability groups"] = "기능 그룹",
        ["Board and image management, defect detection algorithms, processing and tolerance rules, and output traceability are split into dedicated windows so dense inspection work does not crowd the main review screen."] = "보드/이미지 관리, 결함 검출 알고리즘, 처리/공차 규칙, 출력 추적성을 전용 창으로 분리하여 복잡한 검사 작업이 메인 검토 화면을 혼잡하게 만들지 않도록 합니다.",
        ["Recent Jobs"] = "최근 작업",
        ["Current Evidence Boundary"] = "현재 증빙 경계",
        ["Stage 1 image-validation evidence is visible and usable. Real camera, lighting, robot, and MES readiness must be validated through Hardware Readiness evidence before it can be represented as production hardware readiness."] = "1단계 이미지 검증 증빙은 표시되고 사용할 수 있습니다. 실제 카메라, 조명, 로봇, MES 준비성은 운영 하드웨어 준비성으로 표현하기 전에 하드웨어 준비성 증빙으로 검증되어야 합니다.",
        ["Separated-window design rule"] = "분리 창 설계 규칙",
        ["Use the home map to enter the narrow window needed for the task. New features should become a focused page, tab, or dialog when they would crowd inspection, review, settings, or export workflows."] = "홈 맵에서 작업에 필요한 좁은 범위의 창으로 이동하십시오. 새 기능이 검사, 검토, 설정 또는 내보내기 워크플로를 혼잡하게 만들 경우 집중 페이지, 탭 또는 대화 상자로 분리해야 합니다.",
        ["Operator Guardrails"] = "작업자 보호 규칙",
        ["Operator comfort defaults that do not hide audit or hardware boundary information."] = "감사 또는 하드웨어 경계 정보를 숨기지 않는 작업자 편의 기본값입니다.",
        ["Comfort settings cannot suppress critical alarms, hide simulated hardware banners, remove export verification, or bypass audit evidence."] = "편의 설정은 치명 알람 억제, 시뮬레이션 하드웨어 배너 숨김, 내보내기 검증 제거, 감사 증빙 우회를 허용하지 않습니다.",
        ["Keep critical alarms pinned"] = "치명 알람 고정 유지",
        ["Keep simulated hardware boundary visible"] = "시뮬레이션 하드웨어 경계 표시 유지",
        ["Keep failed quality gates visible"] = "실패한 품질 게이트 표시 유지",
        ["review workflow"] = "검토 워크플로",
        ["ROI/rules"] = "ROI/규칙",
        ["stage 1 validation"] = "1단계 검증",
        ["history/package"] = "이력/패키지",
        ["customer evidence"] = "고객 증빙",
        ["sample CSV mode"] = "샘플 CSV 모드",
        ["stage 2 prep"] = "2단계 준비",
        ["setup/docs"] = "설치/문서",
        ["ACTIVE ALARMS"] = "활성 알람",
        ["No active alarms."] = "활성 알람 없음.",
        ["All severities"] = "전체 심각도",
        ["Info"] = "정보",
        ["Warning"] = "경고",
        ["Alarm"] = "알람",
        ["Critical"] = "치명",
        ["Severity high first"] = "심각도 높은 순",
        ["Newest first"] = "최신순",
        ["Oldest first"] = "오래된 순",
        ["Severity low first"] = "심각도 낮은 순",
        ["Acknowledge"] = "확인",
        ["Details"] = "상세",
        ["Export Log"] = "로그 내보내기",
        ["Sample "] = "샘플 ",
        ["Golden "] = "기준 ",
        ["Score "] = "점수 ",
        ["Verdict "] = "판정 ",
        ["Refresh"] = "새로고침",
        ["Lock Recipe"] = "레시피 잠금",
        ["Export"] = "내보내기",
        ["Load a board image, review the marked defects, then save the result. Purple panels mean the action is simulated or mock-only."] = "보드 이미지를 불러오고 표시된 결함을 검토한 뒤 결과를 저장합니다. 보라색 패널은 시뮬레이션 또는 목 전용 동작을 의미합니다.",
        ["Disposition"] = "판정 처리",
        ["Fiducial alignment"] = "피듀셜 정렬",
        ["Golden template"] = "기준 템플릿",
        ["ROI masks"] = "ROI 마스크",
        ["Tolerance rules"] = "공차 규칙",
        ["Lighting boundary"] = "조명 경계",
        ["Image Library"] = "이미지 라이브러리",
        ["Image / Simulated Live Feed"] = "이미지 / 시뮬레이션 실시간 피드",
        ["No image loaded"] = "이미지 없음",
        ["View"] = "보기",
        ["Top"] = "상면",
        ["Side"] = "측면",
        ["Bottom"] = "하면",
        ["Defect overlay layer"] = "결함 오버레이",
        ["Top Folder"] = "상면 폴더",
        ["Side Folder"] = "측면 폴더",
        ["Bottom Folder"] = "하면 폴더",
        ["Auto-save"] = "자동 저장",
        ["Simulated Robot / Handler"] = "시뮬레이션 로봇 / 핸들러",
        ["Simulation only. No real robot, handler, PLC, or safety hardware is connected."] = "시뮬레이션 전용입니다. 실제 로봇, 핸들러, PLC 또는 안전 하드웨어가 연결되어 있지 않습니다.",
        ["Ready: simulation only"] = "준비됨: 시뮬레이션 전용",
        ["No simulated board loaded"] = "시뮬레이션 보드 없음",
        ["No real robot connected"] = "실제 로봇 미연결",
        ["Last cycle --"] = "마지막 사이클 --",
        ["Robot acceptance: not validated"] = "로봇 승인: 미검증",
        ["Fault: none"] = "고장: 없음",
        ["Load"] = "로드",
        ["Inspect"] = "검사",
        ["Unload"] = "언로드",
        ["Cancel"] = "취소",
        ["Reset"] = "재설정",
        ["E-Stop Sim"] = "비상정지 시뮬",
        ["Run Cycle"] = "사이클 실행",
        ["Run Robot Cell Acceptance Test"] = "로봇 셀 승인 테스트 실행",
        ["Export Report"] = "보고서 내보내기",
        ["Select a folder or load an image from Image Library, then press Start / Next Board."] = "폴더를 선택하거나 이미지 라이브러리에서 이미지를 불러온 뒤 시작 / 다음 보드를 누르십시오.",
        ["Start"] = "시작",
        ["Stop"] = "정지",
        ["Next Board"] = "다음 보드",
        ["Save Result"] = "결과 저장",
        ["Board / Status"] = "보드 / 상태",
        ["Station"] = "스테이션",
        ["Board / Model"] = "보드 / 모델",
        ["Lot ID"] = "로트 ID",
        ["Operator"] = "작업자",
        ["Model Version"] = "모델 버전",
        ["2D Cal Profile"] = "2D 보정 프로파일",
        ["Frame"] = "프레임",
        ["Timing"] = "타이밍",
        ["Result"] = "결과",
        ["Defect List"] = "결함 목록",
        ["No"] = "번호",
        ["Type"] = "유형",
        ["ROI"] = "ROI",
        ["ROI Type"] = "ROI 유형",
        ["Placement"] = "배치",
        ["Solder Volume"] = "솔더 체적",
        ["Surface Defect"] = "표면 결함",
        ["Severity"] = "심각도",
        ["Score"] = "점수",
        ["X"] = "X",
        ["Y"] = "Y",
        ["Board X mm"] = "보드 X mm",
        ["Board Y mm"] = "보드 Y mm",
        ["Alarm / Event Log"] = "알람 / 이벤트 로그",
        ["Time"] = "시간",
        ["Event"] = "이벤트",
        ["Message"] = "메시지",
        ["STOPPED"] = "정지됨",
        ["Settings / Guide Work Areas"] = "설정 / 가이드 작업 영역",
        ["Settings Control Center"] = "설정 제어 센터",
        ["Basics"] = "기본",
        ["QOL"] = "편의",
        ["AI"] = "AI",
        ["Hardware"] = "하드웨어",
        ["Traceability"] = "추적성",
        ["Evidence"] = "증빙",
        ["Resolution"] = "해상도",
        ["Theme"] = "테마",
        ["Industrial Dark"] = "산업용 다크",
        ["Industrial Light"] = "산업용 라이트",
        ["1920 x 1080 minimum HMI"] = "1920 x 1080 최소 HMI",
        ["2560 x 1440 engineering monitor"] = "2560 x 1440 엔지니어링 모니터",
        ["3840 x 2160 wall display"] = "3840 x 2160 벽면 디스플레이",
        ["Staged changes"] = "대기 중인 변경",
        ["Apply writes settings and refreshes the shell. Cancel restores the last saved values."] = "적용은 설정을 저장하고 셸을 새로 고칩니다. 취소는 마지막 저장 값을 복원합니다.",
        ["Processing & Tolerance Rules"] = "처리 및 공차 규칙",
        ["X/Y tolerance mm"] = "X/Y 공차 mm",
        ["Rotation tolerance deg"] = "회전 공차 deg",
        ["IPC acceptability class"] = "IPC 허용 등급",
        ["Lighting profile"] = "조명 프로파일",
        ["False-call policy"] = "허위 검출 정책",
        ["Display / Language"] = "화면 / 언어",
        ["Language"] = "언어",
        ["Font Size"] = "글자 크기",
        ["Compact"] = "표준",
        ["Standard"] = "크게",
        ["Large"] = "매우 크게",
        ["Program Assets"] = "프로그램 자산",
        ["Console Title"] = "콘솔 제목",
        ["Station Label"] = "스테이션 라벨",
        ["Station Subtitle"] = "스테이션 설명",
        ["Accent Color"] = "강조 색상",
        ["Logo Image"] = "로고 이미지",
        ["Browse"] = "찾아보기",
        ["Apply"] = "적용",
        ["Test Model Configuration"] = "모델 설정 테스트",
        ["Run Setup Wizard Again"] = "설정 마법사 다시 실행",
        ["Export Diagnostics Report"] = "진단 보고서 내보내기",
        ["Backup Configuration"] = "설정 백업",
        ["Restore Configuration Preview"] = "설정 복원 미리보기",
        ["Apply Restore"] = "복원 적용",
        ["Rollback Last Restore"] = "마지막 복원 되돌리기",
        ["Redact image/storage paths"] = "이미지/저장 경로 숨김",
        ["Include model files"] = "모델 파일 포함",
        ["Export Support Bundle"] = "지원 번들 내보내기",
    };
    private static UiPreferences? _cached;

    public static event Action? PreferencesChanged;

    public static string SettingsPath => Path.Combine(StorageRootSettingsService.SettingsDirectory, "ui_preferences.json");

    public static UiPreferences Load()
    {
        if (_cached is not null)
            return Clone(_cached);

        try
        {
            if (File.Exists(SettingsPath))
            {
                _cached = JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(SettingsPath)) ?? new UiPreferences();
                Normalize(_cached);
                return Clone(_cached);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"UI preferences could not be loaded; defaults will be used: {ex.Message}");
        }

        _cached = new UiPreferences();
        return Clone(_cached);
    }

    public static void Save(UiPreferences preferences)
    {
        Normalize(preferences);
        Directory.CreateDirectory(StorageRootSettingsService.SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(preferences, JsonOptions));
        _cached = Clone(preferences);
        ApplyToApplication(_cached);
        PreferencesChanged?.Invoke();
    }

    public static void ResetForTests()
    {
        _cached = null;
        PreferencesChanged = null;
    }

    public static string Text(string english, string korean)
        => Load().Language == UiLanguage.Korean ? korean : english;

    public static bool AreEquivalent(UiPreferences left, UiPreferences right)
    {
        Normalize(left);
        Normalize(right);
        return left.Language == right.Language &&
            left.FontPreset == right.FontPreset &&
            left.Theme == right.Theme &&
            left.ResolutionPreset == right.ResolutionPreset &&
            string.Equals(left.ConsoleTitle, right.ConsoleTitle, StringComparison.Ordinal) &&
            string.Equals(left.StationDisplayName, right.StationDisplayName, StringComparison.Ordinal) &&
            string.Equals(left.StationSubtitle, right.StationSubtitle, StringComparison.Ordinal) &&
            string.Equals(left.AccentColor, right.AccentColor, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.BrandLogoPath, right.BrandLogoPath, StringComparison.OrdinalIgnoreCase);
    }

    public static void ApplyToApplication(UiPreferences? preferences = null)
    {
        var current = preferences ?? Load();
        var culture = current.Language == UiLanguage.Korean ? new CultureInfo("ko-KR") : new CultureInfo("en-US");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        var app = Application.Current;
        if (app is null)
            return;

        if (!app.Dispatcher.CheckAccess())
        {
            try
            {
                app.Dispatcher.BeginInvoke(() => ApplyWindowPreferences(current, culture));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"UI preferences could not be dispatched to the application: {ex.Message}");
            }

            return;
        }

        ApplyWindowPreferences(current, culture);
    }

    private static void ApplyWindowPreferences(UiPreferences current, CultureInfo culture)
    {
        ApplyThemeResources(current.Theme);

        if (Application.Current?.MainWindow is not Window mainWindow)
            return;

        mainWindow.Language = XmlLanguage.GetLanguage(culture.IetfLanguageTag);
        mainWindow.FontFamily = current.Language == UiLanguage.Korean
            ? new FontFamily("Malgun Gothic, Segoe UI")
            : new FontFamily("Segoe UI");

        if (mainWindow.Content is FrameworkElement root)
            root.LayoutTransform = Transform.Identity;

        mainWindow.FontSize = current.FontPreset switch
        {
            UiFontPreset.Compact => 14,
            UiFontPreset.Large => 17,
            _ => 15,
        };

        var (width, height) = current.ResolutionPreset switch
        {
            UiResolutionPreset.Qhd2560x1440 => (2560d, 1440d),
            UiResolutionPreset.Uhd3840x2160 => (3840d, 2160d),
            _ => (1920d, 1080d),
        };

        mainWindow.MinWidth = 1600;
        mainWindow.MinHeight = 900;
        if (mainWindow.WindowState == WindowState.Normal)
        {
            mainWindow.Width = Math.Max(mainWindow.Width, Math.Min(width, SystemParameters.WorkArea.Width));
            mainWindow.Height = Math.Max(mainWindow.Height, Math.Min(height, SystemParameters.WorkArea.Height));
        }

        ApplyLocalization(mainWindow, current.Language);
    }

    private static void ApplyThemeResources(UiTheme theme)
    {
        var light = theme == UiTheme.IndustrialLight;
        SetBrush("Bg", light ? "#EEF3F7" : "#0B0E10");
        SetBrush("WindowBg", light ? "#F7FAFC" : "#121619");
        SetBrush("FrameBg", light ? "#FFFFFF" : "#252C31");
        SetBrush("Frame2Bg", light ? "#EDF3F7" : "#1B2024");
        SetBrush("CellBg", light ? "#FFFFFF" : "#151A1E");
        SetBrush("Cell2Bg", light ? "#F1F5F8" : "#1C2328");
        SetBrush("LineBrush", light ? "#CBD5DF" : "#3D464D");
        SetBrush("TextBrush", light ? "#0D1B2A" : "#D8DEE3");
        SetBrush("MutedBrush", light ? "#526170" : "#8B969E");
        SetBrush("DimBrush", light ? "#6B7A88" : "#667078");

        SetBrush("HmiBgBrush", light ? "#EEF3F7" : "#0B0E10");
        SetBrush("HmiSurfaceBrush", light ? "#FFFFFF" : "#151A1E");
        SetBrush("HmiSurfaceAltBrush", light ? "#EDF3F7" : "#1B2024");
        SetBrush("HmiBorderBrush", light ? "#CBD5DF" : "#3E474E");
        SetBrush("HmiTextBrush", light ? "#0D1B2A" : "#E8EEF2");
        SetBrush("HmiMutedTextBrush", light ? "#526170" : "#A8B2B9");
    }

    private static void SetBrush(string key, string color)
    {
        if (Application.Current is null)
            return;

        if (Application.Current.TryFindResource(key) is SolidColorBrush existing)
        {
            if (existing.IsFrozen)
            {
                Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                return;
            }

            existing.Color = (Color)ColorConverter.ConvertFromString(color);
            return;
        }

        Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    public static void ApplyLocalization(DependencyObject root)
        => ApplyLocalization(root, Load().Language);

    public static void ApplyLocalization(DependencyObject root, UiLanguage language)
    {
        var visited = new HashSet<DependencyObject>();
        ApplyLocalizationCore(root, language, visited);
    }

    private static void ApplyLocalizationCore(DependencyObject root, UiLanguage language, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(root))
            return;

        if (root is TextBlock textBlock && textBlock.Name != "PageTitleText")
        {
            var original = GetOrStore(textBlock, OriginalTextProperty, textBlock.Text);
            textBlock.Text = Translate(original, language);
        }

        if (root is ContentControl contentControl && contentControl.Content is string content)
        {
            var original = GetOrStore(contentControl, OriginalContentProperty, content);
            contentControl.Content = Translate(original, language);
        }

        if (root is HeaderedContentControl headered && headered.Header is string header)
        {
            var original = GetOrStore(headered, OriginalHeaderProperty, header);
            headered.Header = Translate(original, language);
        }

        if (root is HeaderedItemsControl headeredItems && headeredItems.Header is string itemHeader)
        {
            var original = GetOrStore(headeredItems, OriginalHeaderProperty, itemHeader);
            headeredItems.Header = Translate(original, language);
        }

        if (root is FrameworkElement element && element.ToolTip is string toolTip)
        {
            var original = GetOrStore(element, OriginalToolTipProperty, toolTip);
            element.ToolTip = Translate(original, language);
        }

        if (root is DataGrid dataGrid)
        {
            foreach (var column in dataGrid.Columns)
            {
                if (column.Header is not string columnHeader)
                    continue;

                var original = GetOrStore(column, OriginalHeaderProperty, columnHeader);
                column.Header = Translate(original, language);
            }
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            ApplyLocalizationCore(child, language, visited);

        if (root is not Visual and not Visual3D)
            return;

        var visualChildren = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < visualChildren; i++)
            ApplyLocalizationCore(VisualTreeHelper.GetChild(root, i), language, visited);
    }

    private static string GetOrStore(DependencyObject owner, DependencyProperty property, string current)
    {
        if (owner.GetValue(property) is string original)
            return original;

        owner.SetValue(property, current);
        return current;
    }

    private static string Translate(string original, UiLanguage language)
        => language == UiLanguage.Korean && KoreanText.TryGetValue(original, out var translated)
            ? translated
            : original;

    public static Brush AccentBrush(UiPreferences? preferences = null)
    {
        var current = preferences ?? Load();
        Normalize(current);
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(current.AccentColor));
    }

    private static void Normalize(UiPreferences preferences)
    {
        if (!Enum.IsDefined(preferences.Language))
            preferences.Language = UiLanguage.English;
        if (!Enum.IsDefined(preferences.FontPreset))
            preferences.FontPreset = UiFontPreset.Standard;
        if (!Enum.IsDefined(preferences.Theme))
            preferences.Theme = UiTheme.IndustrialDark;
        if (!Enum.IsDefined(preferences.ResolutionPreset))
            preferences.ResolutionPreset = UiResolutionPreset.FullHd1920x1080;
        preferences.ConsoleTitle = NormalizeDisplayText(preferences.ConsoleTitle, UiPreferenceDefaults.ConsoleTitle);
        preferences.StationDisplayName = NormalizeDisplayText(preferences.StationDisplayName, UiPreferenceDefaults.StationDisplayName);
        preferences.StationSubtitle = NormalizeDisplayText(preferences.StationSubtitle, UiPreferenceDefaults.StationSubtitle);
        preferences.AccentColor = NormalizeColor(preferences.AccentColor);
        preferences.BrandLogoPath = preferences.BrandLogoPath?.Trim() ?? string.Empty;
    }

    private static UiPreferences Clone(UiPreferences source)
        => new()
        {
            Language = source.Language,
            FontPreset = source.FontPreset,
            Theme = source.Theme,
            ResolutionPreset = source.ResolutionPreset,
            ConsoleTitle = source.ConsoleTitle,
            StationDisplayName = source.StationDisplayName,
            StationSubtitle = source.StationSubtitle,
            AccentColor = source.AccentColor,
            BrandLogoPath = source.BrandLogoPath,
        };

    private static string NormalizeDisplayText(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string NormalizeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return UiPreferenceDefaults.AccentColor;

        try
        {
            _ = (Color)ColorConverter.ConvertFromString(value.Trim());
            return value.Trim();
        }
        catch (FormatException)
        {
            return UiPreferenceDefaults.AccentColor;
        }
        catch (NotSupportedException)
        {
            return UiPreferenceDefaults.AccentColor;
        }
    }
}
