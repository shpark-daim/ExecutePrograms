using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ExecutePrograms.Models;
using ExecutePrograms.Services;

namespace ExecutePrograms;

public partial class MainWindow : Window
{
    private const string XmsExeDir   = @"C:\Project\Release\xms\xms";
    private const string XflowExeDir = @"C:\Project\xflow\Src\xflow\bin\Debug\net10.0";

    private static readonly string AppDir      = AppContext.BaseDirectory;
    private static readonly string SettingsPath = Path.Combine(AppDir, "settings.json");
    private static readonly string ProfilesPath = Path.Combine(AppDir, "profiles.json");

    private Dictionary<string, AppSettings> _profiles = new();
    private bool _suppressProfileSelection = false;

    private readonly ObservableCollection<ExtraProgram> _extraPrograms = new();
    private string _fexDir = @"C:\DAIM\FexHub";

    public MainWindow()
    {
        InitializeComponent();
        ExtraProgramsList.ItemsSource  = _extraPrograms;
        ExtraStopChecks.ItemsSource    = _extraPrograms;
        ExtraStartChecks.ItemsSource   = _extraPrograms;
        LoadSettings();
        LoadProfiles();
        Closing += (_, _) => SaveSettings();
    }

    // ── 설정 저장 / 불러오기 ──────────────────────────────

    private void LoadSettings()
    {
        try
        {
            AppSettings s = new();
            if (File.Exists(SettingsPath))
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            ApplySettings(s);
        }
        catch { /* 오류 무시 */ }
    }

    private void SaveSettings()
    {
        try
        {
            var s = CaptureCurrent();
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"설정 저장 실패:\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── 설정 캡처 / 적용 헬퍼 ────────────────────────────

    private AppSettings CaptureCurrent() => new()
    {
        XmsMapPath   = XmsMapPath.Text,
        XflowMapPath = XflowMapPath.Text,
        SqlFilePath  = SqlFilePath.Text,
        BatDir  = BatDir.Text,
        ExeDir  = ExeDir.Text,
        FexDir  = _fexDir,
        DbHost       = DbHost.Text,
        DbPort       = DbPort.Text,
        DbName       = DbName.Text,
        DbUser       = DbUser.Text,
        DbPassword   = DbPassword.Password,
        BatStart      = BatStartCheck.IsChecked  == true,
        BatStop       = BatStopCheck.IsChecked   == true,
        ExeStart      = ExeStartCheck.IsChecked  == true,
        ExeStop       = ExeStopCheck.IsChecked   == true,
        FexStart      = FexStartCheck.IsChecked  == true,
        FexStop       = FexStopCheck.IsChecked   == true,
        XmsDbRun      = XmsDbCheck.IsChecked     == true,
        XflowDbRun    = XflowDbCheck.IsChecked   == true,
        SqlRun        = SqlCheck.IsChecked        == true,
        FexWorkspace  = FexWorkspace.Text,
        ExtraPrograms = [.. _extraPrograms],
    };

    private void ApplySettings(AppSettings s)
    {
        XmsMapPath.Text   = s.XmsMapPath;
        XflowMapPath.Text = s.XflowMapPath;
        SqlFilePath.Text  = s.SqlFilePath;
        BatDir.Text = s.BatDir;
        ExeDir.Text = s.ExeDir;
        _fexDir     = s.FexDir;
        DbHost.Text         = s.DbHost;
        DbPort.Text         = s.DbPort;
        DbName.Text         = s.DbName;
        DbUser.Text         = s.DbUser;
        DbPassword.Password = s.DbPassword;
        BatStartCheck.IsChecked  = s.BatStart;
        BatStopCheck.IsChecked   = s.BatStop;
        ExeStartCheck.IsChecked  = s.ExeStart;
        ExeStopCheck.IsChecked   = s.ExeStop;
        FexStartCheck.IsChecked  = s.FexStart;
        FexStopCheck.IsChecked   = s.FexStop;
        XmsDbCheck.IsChecked     = s.XmsDbRun;
        XflowDbCheck.IsChecked   = s.XflowDbRun;
        SqlCheck.IsChecked       = s.SqlRun;
        FexWorkspace.Text       = s.FexWorkspace;
        if (!string.IsNullOrEmpty(s.XmsMapPath))
            XmsCmd2Preview.Text = $@".\xms.exe db create ""{s.XmsMapPath}""";
        if (!string.IsNullOrEmpty(s.XflowMapPath))
            XflowCmd2Preview.Text = $@".\xflow.exe db create -m ""{s.XflowMapPath}""";

        _extraPrograms.Clear();
        var list = s.ExtraPrograms is { Count: > 0 } ? s.ExtraPrograms : [new ExtraProgram()];
        foreach (var p in list) _extraPrograms.Add(p);
    }

    // ── 프로필 저장 / 불러오기 ────────────────────────────

    private void LoadProfiles()
    {
        try
        {
            if (File.Exists(ProfilesPath))
                _profiles = JsonSerializer.Deserialize<Dictionary<string, AppSettings>>(
                    File.ReadAllText(ProfilesPath)) ?? new();
        }
        catch { _profiles = new(); }

        RefreshProfileComboBox(null);
    }

    private void SaveProfiles()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProfilesPath)!);
            File.WriteAllText(ProfilesPath,
                JsonSerializer.Serialize(_profiles, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"프로필 저장 실패:\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshProfileComboBox(string? selectedName)
    {
        _suppressProfileSelection = true;
        ProfileComboBox.Items.Clear();
        foreach (var name in _profiles.Keys.OrderBy(k => k))
            ProfileComboBox.Items.Add(name);

        ProfileComboBox.Text = selectedName ?? string.Empty;
        _suppressProfileSelection = false;
    }

    // ── 찾기 버튼들 ─────────────────────────────────────

    private void BrowseXmsMap_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "xms — Map 파일 선택", Filter = "JSON (*.json)|*.json|모든 파일|*.*" };
        if (dlg.ShowDialog() != true) return;
        XmsMapPath.Text     = dlg.FileName;
        XmsCmd2Preview.Text = $@".\xms.exe db create ""{dlg.FileName}""";
        SaveSettings();
    }

    private void BrowseXflowMap_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "xflow — Map 파일 선택", Filter = "JSON (*.json)|*.json|모든 파일|*.*" };
        if (dlg.ShowDialog() != true) return;
        XflowMapPath.Text     = dlg.FileName;
        XflowCmd2Preview.Text = $@".\xflow.exe db create -m ""{dlg.FileName}""";
        SaveSettings();
    }

    private void BrowseSqlFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "SQL 파일 선택", Filter = "SQL (*.sql)|*.sql|모든 파일|*.*" };
        if (dlg.ShowDialog() != true) return;
        SqlFilePath.Text = dlg.FileName;
        SaveSettings();
    }

    private void BrowseBatDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "BAT — 실행 위치 선택" };
        if (dlg.ShowDialog() != true) return;
        BatDir.Text = dlg.FolderName;
        SaveSettings();
    }

    private void BrowseExeDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "xflowd — 실행 위치 선택" };
        if (dlg.ShowDialog() != true) return;
        ExeDir.Text = dlg.FolderName;
        SaveSettings();
    }



    // ── FEX 워크스페이스 자동화 ──────────────────────────────

    private async Task AutomateFexStartupAsync(Process fexProcess, string workspace)
    {
        var target = string.IsNullOrWhiteSpace(workspace) ? "Default" : workspace;
        Log($"  FEX 시작 창 대기 중... (워크스페이스: {target})");

        // 최대 20초 동안 FEX 창이 뜰 때까지 대기
        AutomationElement? fexWindow = null;
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(1000);
            try
            {
                fexWindow = AutomationElement.RootElement.FindFirst(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, fexProcess.Id));
                if (fexWindow != null) break;
            }
            catch { /* 아직 창이 없으면 재시도 */ }
        }

        if (fexWindow == null)
        {
            Log("  ⚠ FEX 시작 창을 찾지 못했습니다 (수동으로 워크스페이스를 선택하세요)");
            return;
        }

        // ComboBox (Current Workspace) 찾기
        var combo = fexWindow.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox));

        if (combo == null)
        {
            Log("  ⚠ 워크스페이스 목록을 찾지 못했습니다");
        }
        else
        {
            try
            {
                // 드롭다운 열기
                if (combo.GetCurrentPattern(ExpandCollapsePattern.Pattern) is ExpandCollapsePattern expand)
                {
                    expand.Expand();
                    await Task.Delay(300);
                }

                // 항목 선택
                var item = combo.FindFirst(TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                        new PropertyCondition(AutomationElement.NameProperty, target)));

                if (item == null && target != "Default")
                {
                    Log($"  ⚠ '{target}' 없음 — Default로 대체");
                    item = combo.FindFirst(TreeScope.Descendants,
                        new AndCondition(
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                            new PropertyCondition(AutomationElement.NameProperty, "Default")));
                }

                if (item?.GetCurrentPattern(SelectionItemPattern.Pattern) is SelectionItemPattern select)
                {
                    select.Select();
                    Log($"  ✓ 워크스페이스 선택: {item.Current.Name}");
                }

                await Task.Delay(200);
            }
            catch (Exception ex) { Log($"  ⚠ 워크스페이스 선택 실패: {ex.Message}"); }
        }

        // Start 버튼 클릭
        var startBtn = fexWindow.FindFirst(TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "Start")));

        if (startBtn?.GetCurrentPattern(InvokePattern.Pattern) is InvokePattern invoke)
        {
            await Task.Delay(200);
            invoke.Invoke();
            Log("  ✓ FEX Start 완료");
        }
        else
        {
            Log("  ⚠ Start 버튼을 찾지 못했습니다 (수동으로 눌러주세요)");
        }
    }

    // ── 추가 프로그램 핸들러 ─────────────────────────────────

    private void AddExtraProgram_Click(object sender, RoutedEventArgs e)
    {
        _extraPrograms.Add(new ExtraProgram());
        SaveSettings();
    }

    private void BrowseExtraExe_Click(object sender, RoutedEventArgs e)
    {
        var item = (ExtraProgram)((FrameworkElement)sender).DataContext;
        var dlg  = new OpenFileDialog { Filter = "실행 파일 (*.exe)|*.exe|모든 파일|*.*" };
        if (dlg.ShowDialog() != true) return;
        item.ExePath = dlg.FileName;
        if (string.IsNullOrEmpty(item.DisplayName))
            item.DisplayName = Path.GetFileNameWithoutExtension(dlg.FileName);
        SaveSettings();
    }

    private void RemoveExtraProgram_Click(object sender, RoutedEventArgs e)
    {
        var item = (ExtraProgram)((FrameworkElement)sender).DataContext;
        _extraPrograms.Remove(item);
        SaveSettings();
    }

    // ── 프로필 버튼 핸들러 ────────────────────────────────

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = ProfileComboBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("프로필 이름을 입력하거나 선택하세요.", "프로필 저장",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_profiles.ContainsKey(name))
        {
            var result = MessageBox.Show($"'{name}' 프로필을 덮어쓰시겠습니까?", "프로필 저장",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
        }

        _profiles[name] = CaptureCurrent();
        SaveProfiles();
        RefreshProfileComboBox(name);
        Log($"프로필 저장됨: {name}");
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = ProfileComboBox.Text?.Trim();
        if (string.IsNullOrEmpty(name) || !_profiles.ContainsKey(name))
        {
            MessageBox.Show("삭제할 프로필을 선택하세요.", "프로필 삭제",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show($"'{name}' 프로필을 삭제하시겠습니까?", "프로필 삭제",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _profiles.Remove(name);
        SaveProfiles();
        RefreshProfileComboBox(null);
        Log($"프로필 삭제됨: {name}");
    }

    private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProfileSelection) return;
        var name = ProfileComboBox.SelectedItem as string;
        if (name == null || !_profiles.TryGetValue(name, out var settings)) return;

        ApplySettings(settings);
        SaveSettings();
        Log($"프로필 불러옴: {name}");
    }

    // ── 실행 버튼 ────────────────────────────────────────

    private async void ExecuteBtn_Click(object sender, RoutedEventArgs e)
    {
        ExecuteBtn.IsEnabled = false;
        ExecuteBtn.Content   = "⏳  실행 중...";
        LogBox.Clear();
        ResetAllSteps();

        var cmd = new CommandRunner(Log);
        var db  = new DbInitializer(Log);
        var prg = new ProcessLauncher(Log);

        // ① 종료
        SetStep(1, StepState.Active);
        Log("════ ① 종료 단계 ════");

        if (BatStopCheck.IsChecked == true && !string.IsNullOrWhiteSpace(BatDir.Text))
        {
            try
            {
                Log("  xms service stop (최대 30초 대기)");
                await cmd.RunAsync(BatDir.Text, @".\xms.exe service stop", timeoutMs: 30_000);
            }
            catch (Exception ex) { Log($"  ⚠ xms service stop 건너뜀: {ex.Message}"); }
        }
        else Log("  xms 서비스 — 건너뜀");

        if (ExeStopCheck.IsChecked == true)
        {
            try
            {
                Log("  xflowd.exe 프로세스 종료");
                await prg.KillProcessByNameAsync("xflowd.exe");
            }
            catch (Exception ex) { Log($"  ⚠ xflowd 건너뜀: {ex.Message}"); }
        }
        else Log("  xflowd — 건너뜀");

        if (FexStopCheck.IsChecked == true)
        {
            try
            {
                Log("  Fex.exe 프로세스 종료");
                await prg.KillProcessByNameAsync("Fex.exe");
            }
            catch (Exception ex) { Log($"  ⚠ Fex 건너뜀: {ex.Message}"); }
        }
        else Log("  FEX — 건너뜀");

        foreach (var p in _extraPrograms.Where(p => p.StopEnabled && !string.IsNullOrWhiteSpace(p.ExePath)))
        {
            try
            {
                var label = string.IsNullOrWhiteSpace(p.DisplayName) ? p.FileName : p.DisplayName;
                Log($"  {label} 종료");
                await prg.KillProcessByNameAsync(p.FileName);
            }
            catch (Exception ex) { Log($"  ⚠ 건너뜀: {ex.Message}"); }
        }

        SetStep(1, StepState.Done);

        // ② xms DB
        SetStep(2, StepState.Active);
        Log("════ ② xms DB ════");
        if (XmsDbCheck.IsChecked != true)
        {
            Log("  ○ 건너뜀");
            SetStep(2, StepState.Skipped);
        }
        else
        {
            try
            {
                Log("  db drop -a");
                await cmd.RunAsync(XmsExeDir, @".\xms.exe db drop -a");

                if (!string.IsNullOrWhiteSpace(XmsMapPath.Text))
                {
                    Log("  db create");
                    await cmd.RunAsync(XmsExeDir, $@".\xms.exe db create ""{XmsMapPath.Text}""");
                }
                else Log("  Map 미설정 — db create 건너뜀");

                SetStep(2, StepState.Done);
            }
            catch (Exception ex) { SetStep(2, StepState.Error); Log($"  ❌ {ex.Message}"); }
        }

        // ③ xflow DB
        SetStep(3, StepState.Active);
        Log("════ ③ xflow DB ════");
        if (XflowDbCheck.IsChecked != true)
        {
            Log("  ○ 건너뜀");
            SetStep(3, StepState.Skipped);
        }
        else
        {
            try
            {
                Log("  db admin drop -a");
                await cmd.RunAsync(XflowExeDir, @".\xflow.exe db admin drop -a");

                if (!string.IsNullOrWhiteSpace(XflowMapPath.Text))
                {
                    Log("  db create");
                    await cmd.RunAsync(XflowExeDir, $@".\xflow.exe db create -m ""{XflowMapPath.Text}""");
                }
                else Log("  Map 미설정 — db create 건너뜀");

                SetStep(3, StepState.Done);
            }
            catch (Exception ex) { SetStep(3, StepState.Error); Log($"  ❌ {ex.Message}"); }
        }

        // ④ SQL
        SetStep(4, StepState.Active);
        Log("════ ④ PostgreSQL SQL ════");
        if (SqlCheck.IsChecked != true)
        {
            Log("  ○ 건너뜀");
            SetStep(4, StepState.Skipped);
        }
        else
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(SqlFilePath.Text))
                    await db.RunSqlFileAsync(DbHost.Text, DbPort.Text, DbName.Text,
                                             DbUser.Text, DbPassword.Password, SqlFilePath.Text);
                else
                    Log("  SQL 파일 미설정, 건너뜀");

                SetStep(4, StepState.Done);
            }
            catch (Exception ex) { SetStep(4, StepState.Error); Log($"  ❌ {ex.Message}"); }
        }

        // ⑤ 시작
        SetStep(5, StepState.Active);
        Log("════ ⑤ 시작 단계 ════");
        try
        {
            if (BatStartCheck.IsChecked == true && !string.IsNullOrWhiteSpace(BatDir.Text))
            {
                Log("  xms service start");
                await cmd.RunAsync(BatDir.Text, @".\xms.exe service start", timeoutMs: 30_000);
            }
            else Log("  xms 서비스 — 건너뜀");

            if (ExeStartCheck.IsChecked == true && !string.IsNullOrWhiteSpace(ExeDir.Text))
            {
                Log("  xflowd.exe 실행");
                await prg.StartInDirAsync(ExeDir.Text, "xflowd.exe");
            }
            else Log("  xflowd — 건너뜀");

            if (FexStartCheck.IsChecked == true && !string.IsNullOrWhiteSpace(_fexDir))
            {
                Log("  Fex Hub.exe 실행");
                var fexProcess = Process.Start(new ProcessStartInfo(
                    Path.Combine(_fexDir, "Fex Hub.exe"))
                {
                    UseShellExecute  = true,
                    WorkingDirectory = _fexDir
                });
                if (fexProcess != null)
                    await AutomateFexStartupAsync(fexProcess, FexWorkspace.Text.Trim());
            }
            else Log("  FEX — 건너뜀");

            foreach (var p in _extraPrograms.Where(p => p.StartEnabled && !string.IsNullOrWhiteSpace(p.ExePath)))
            {
                var dir   = Path.GetDirectoryName(p.ExePath) ?? "";
                var label = string.IsNullOrWhiteSpace(p.DisplayName) ? p.FileName : p.DisplayName;
                Log($"  {label} 실행");
                await prg.StartInDirAsync(dir, p.FileName, p.ShowWindow);
            }

            SetStep(5, StepState.Done);
        }
        catch (Exception ex) { SetStep(5, StepState.Error); Log($"  ❌ {ex.Message}"); }

        Log("════ ✅ 완료 ════");
        ExecuteBtn.IsEnabled = true;
        ExecuteBtn.Content   = "▶  실행";
    }

    // ── 단계 상태 관리 ─────────────────────────────────────

    private enum StepState { Inactive, Active, Done, Error, Skipped }

    private static readonly (string Bg, string Bd, string Fg)[] ActiveColors =
    [
        default,
        ("#FFEDEE", "#D73A3A", "#D73A3A"),  // ① 종료
        ("#EBF0FF", "#1F6FEB", "#1F6FEB"),  // ② xms DB
        ("#F5EFFF", "#8250DF", "#8250DF"),  // ③ xflow DB
        ("#DDFBF0", "#0A8070", "#0A8070"),  // ④ SQL
        ("#EEFFEC", "#2DA44E", "#2DA44E"),  // ⑤ 시작
    ];

    private static readonly string[] ShortNames =
        ["", "종료", "xms DB", "xflow DB", "SQL", "시작"];

    private static readonly string[] OriginalTexts =
        ["", "○", "○", "○", "○", "○"];

    private void SetStep(int step, StepState state)
    {
        Dispatcher.Invoke(() =>
        {
            var (border, text) = step switch
            {
                1 => ((Border)Step1Border, (TextBlock)Step1Text),
                2 => (Step2Border, Step2Text),
                3 => (Step3Border, Step3Text),
                4 => (Step4Border, Step4Text),
                5 => (Step5Border, Step5Text),
                _ => default
            };
            if (border is null) return;

            var (bg, bd, fg) = state switch
            {
                StepState.Active   => ActiveColors[step],
                StepState.Done     => ActiveColors[step],
                StepState.Error    => ("#FFD6E0", "#FF85A1", "#B31D28"),
                StepState.Skipped  => ("#F6F8FA", "#C8D0D8", "#A0AABB"),
                _                  => ("#F6F8FA", "#D0D7DE", "#8B949E"),
            };

            border.Background  = ToBrush(bg);
            border.BorderBrush = ToBrush(bd);
            text.Foreground    = ToBrush(fg);
            text.Text = state switch
            {
                StepState.Active   => $"⏳ {ShortNames[step]}",
                StepState.Done     => $"✓  {ShortNames[step]}",
                StepState.Error    => $"✗  {ShortNames[step]}",
                StepState.Skipped  => $"○  {ShortNames[step]}",
                _                  => OriginalTexts[step],
            };
        });
    }

    private void ResetAllSteps()
    {
        for (int i = 1; i <= 5; i++)
            SetStep(i, StepState.Inactive);
    }

    private static SolidColorBrush ToBrush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    // ── 전체 선택 ──────────────────────────────────────────

    private bool _suppressSelectAll;

    private void SelectAllCheck_Click(object sender, RoutedEventArgs e)
    {
        // 클릭 시 indeterminate → checked 로 강제 (토글: 전체체크 ↔ 전체해제)
        bool v = SelectAllCheck.IsChecked == true;

        _suppressSelectAll = true;
        BatStopCheck.IsChecked   = v;
        ExeStopCheck.IsChecked   = v;
        FexStopCheck.IsChecked   = v;
        XmsDbCheck.IsChecked     = v;
        XflowDbCheck.IsChecked   = v;
        SqlCheck.IsChecked       = v;
        BatStartCheck.IsChecked  = v;
        ExeStartCheck.IsChecked  = v;
        FexStartCheck.IsChecked  = v;
        foreach (var p in _extraPrograms) { p.StopEnabled = v; p.StartEnabled = v; }
        _suppressSelectAll = false;
    }

    private void IndividualCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectAll || !IsLoaded) return;
        UpdateSelectAllState();
    }

    private void UpdateSelectAllState()
    {
        bool?[] states =
        [
            BatStopCheck.IsChecked,  ExeStopCheck.IsChecked,  FexStopCheck.IsChecked,
            XmsDbCheck.IsChecked,    XflowDbCheck.IsChecked,  SqlCheck.IsChecked,
            BatStartCheck.IsChecked, ExeStartCheck.IsChecked, FexStartCheck.IsChecked,
        ];
        bool allOn  = states.All(s => s == true);
        bool allOff = states.All(s => s != true);

        _suppressSelectAll = true;
        SelectAllCheck.IsChecked = allOn ? true : allOff ? false : null;  // null = indeterminate
        _suppressSelectAll = false;
    }

    // ── 로그 헬퍼 ─────────────────────────────────────────

    private void LogClear_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void Log(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}]  {message}{Environment.NewLine}");
            LogScroll.ScrollToBottom();
        });
    }
}
