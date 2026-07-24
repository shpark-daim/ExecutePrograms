using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
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
    private static readonly string AppDir      = AppContext.BaseDirectory;
    private static readonly string SettingsPath = Path.Combine(AppDir, "settings.json");
    private static readonly string ProfilesPath = Path.Combine(AppDir, "profiles.json");

    private Dictionary<string, AppSettings> _profiles = new();
    private bool _suppressProfileSelection = false;

    private readonly ObservableCollection<ExtraProgram> _extraPrograms = new();
    private string _fexDir = "";

    private CancellationTokenSource? _pollCts;

    public MainWindow()
    {
        InitializeComponent();
        ExtraProgramsList.ItemsSource  = _extraPrograms;
        ExtraStopChecks.ItemsSource    = _extraPrograms;
        ExtraStartChecks.ItemsSource   = _extraPrograms;
        LoadSettings();
        LoadProfiles();
        Closing += (_, _) => { StopPolling(); SaveSettings(); };
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

    // ── 설정 패널 토글 / 고정 ────────────────────────────────

    private void CollapseSettings_Click(object sender, RoutedEventArgs e)
        => SetSettingsPanelVisible(CollapseSettingsBtn.IsChecked == true);

    private void SetSettingsPanelVisible(bool visible)
    {
        SettingsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        // Col0 = 실행 패널 (300px 고정, 설정 닫히면 전체 차지)
        RootGrid.ColumnDefinitions[0].Width = visible ? new GridLength(300) : new GridLength(1, GridUnitType.Star);
        RootGrid.ColumnDefinitions[1].Width = visible ? new GridLength(8)   : new GridLength(0);
        // Col2 = 설정 패널 (* 가변, 설정 닫히면 0)
        RootGrid.ColumnDefinitions[2].Width = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        // ToggleButton 상태 동기화 (ApplySettings 등 직접 호출 시)
        CollapseSettingsBtn.IsChecked = visible;
        CollapseSettingsBtn.ToolTip   = visible ? "설정 접기" : "설정 펼치기";
    }

    // ── 서비스 상태 폴링 ─────────────────────────────────

    private void StartPolling()
    {
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;
        _ = Task.Run(() => PollLoopAsync(token));
    }

    private void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var svc = new ServiceStatusChecker(_ => { });   // 로그 없이 조용히 폴링

        while (!ct.IsCancellationRequested)
        {
            // xms — 컴포넌트별 멀티라인 파싱
            string batDir  = "";
            bool   showXms = false;
            Dispatcher.Invoke(() =>
            {
                batDir  = BatDir.Text.Trim();
                showXms = XmsServiceBlock.Visibility == Visibility.Visible;
            });

            if (showXms && !string.IsNullOrWhiteSpace(batDir))
            {
                try
                {
                    var components = await svc.CheckAllAsync(batDir, "xms.exe");
                    if (!ct.IsCancellationRequested)
                        Dispatcher.Invoke(() => UpdateXmsComponents(components));
                }
                catch { /* 무시 */ }
            }

            // xflow — 서비스 모드일 때만 폴링
            if (!ct.IsCancellationRequested)
            {
                string xflowSvcDir = "";
                bool   useService  = false;
                Dispatcher.Invoke(() =>
                {
                    xflowSvcDir = XflowSvcDir.Text.Trim();
                    useService  = XflowSvcRadio.IsChecked == true;
                });

                if (useService && !string.IsNullOrWhiteSpace(xflowSvcDir))
                {
                    try
                    {
                        var components = await svc.CheckAllAsync(xflowSvcDir, "xflow.exe");
                        if (!ct.IsCancellationRequested)
                            Dispatcher.Invoke(() => UpdateXflowComponents(components));
                    }
                    catch { /* 무시 */ }
                }
            }

            try { await Task.Delay(5000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>xms 컴포넌트 목록을 WrapPanel에 동적으로 그립니다.</summary>
    private void UpdateXmsComponents(List<(string Name, ServiceStatusChecker.Result Status)> components)
    {
        XmsPlaceholder.Visibility     = Visibility.Collapsed;
        XmsComponentsPanel.Visibility = Visibility.Visible;
        XmsComponentsPanel.Children.Clear();

        var workingDir = BatDir.Text.Trim();

        foreach (var (name, result) in components)
        {
            var sp = MakeComponentChip(name, result, workingDir, "xms.exe");
            XmsComponentsPanel.Children.Add(sp);
        }
    }

    /// <summary>xflow 컴포넌트 목록을 WrapPanel에 동적으로 그립니다.</summary>
    private void UpdateXflowComponents(List<(string Name, ServiceStatusChecker.Result Status)> components)
    {
        XflowPlaceholder.Visibility     = Visibility.Collapsed;
        XflowComponentsPanel.Visibility = Visibility.Visible;
        XflowComponentsPanel.Children.Clear();

        var workingDir = XflowSvcDir.Text.Trim();

        foreach (var (name, result) in components)
        {
            var sp = MakeComponentChip(name, result, workingDir, "xflow.exe");
            XflowComponentsPanel.Children.Add(sp);
        }
    }

    /// <summary>컴포넌트 상태 칩(StackPanel)을 생성합니다. 우클릭 시 서비스 제어 메뉴가 표시됩니다.</summary>
    private StackPanel MakeComponentChip(
        string name, ServiceStatusChecker.Result result,
        string workingDir, string exeName)
    {
        var (dotColor, textColor) = result.Kind switch
        {
            ServiceStatusChecker.State.Running => ("#2EA043", "#1A6B2A"),
            ServiceStatusChecker.State.Pending => ("#FFC107", "#7D5600"),
            _                                  => ("#E53935", "#C62828"),
        };

        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 0, 10, 2),
            Cursor      = System.Windows.Input.Cursors.Hand,
            ToolTip     = "우클릭 — 서비스 제어",
        };

        sp.Children.Add(new TextBlock
        {
            Text              = "●",
            FontSize          = 9,
            Foreground        = ToBrush(dotColor),
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 3, 0),
        });
        sp.Children.Add(new TextBlock
        {
            Text              = name,
            FontSize          = 11,
            FontWeight        = FontWeights.SemiBold,
            Foreground        = ToBrush("#57606A"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 3, 0),
        });
        sp.Children.Add(new TextBlock
        {
            Text              = result.Raw,
            FontSize          = 11,
            Foreground        = ToBrush(textColor),
            VerticalAlignment = VerticalAlignment.Center,
        });

        // 우클릭 컨텍스트 메뉴
        var menu = new ContextMenu();
        menu.Items.Add(MakeMenuItem("▶  시작", () => _ = ComponentActionAsync(workingDir, exeName, name, "start")));
        menu.Items.Add(MakeMenuItem("■  중지", () => _ = ComponentActionAsync(workingDir, exeName, name, "stop")));
        sp.ContextMenu = menu;

        return sp;
    }

    private static MenuItem MakeMenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>xms / xflow 개별 컴포넌트에 서비스 명령(restart / start / stop)을 실행합니다.</summary>
    private async Task ComponentActionAsync(string workingDir, string exeName, string componentName, string action)
    {
        var label = action switch
        {
            "restart" => $"↺ {componentName} 재시작",
            "start"   => $"▶ {componentName} 시작",
            "stop"    => $"■ {componentName} 중지",
            _         => $"{componentName} {action}",
        };

        Log($"  {label} 중...");

        var psi = new ProcessStartInfo(
            Path.Combine(workingDir, exeName),
            $"service {action} {componentName}")
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        try
        {
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var output = (await stdout).Trim();

            if (proc.ExitCode == 0)
                Log($"  ✓ {label} 완료{(string.IsNullOrEmpty(output) ? "" : $" — {output}")}");
            else
                Log($"  ⚠ {label} 실패 (종료 코드: {proc.ExitCode}){(string.IsNullOrEmpty(output) ? "" : $" — {output}")}");
        }
        catch (Exception ex)
        {
            Log($"  ❌ {label} 오류: {ex.Message}");
        }
    }

    // ── 설정 캡처 / 적용 헬퍼 ────────────────────────────
    private AppSettings CaptureCurrent() => new()
    {
        XmsMapPath   = XmsMapPath.Text,
        XmsDbDir     = XmsDbDir.Text,
        XflowMapPath = XflowMapPath.Text,
        XflowDbDir   = XflowDbDir.Text,
        SqlFilePath  = SqlFilePath.Text,
        BatDir        = BatDir.Text,
        ExeDir        = ExeDir.Text,
        XflowSvcDir   = XflowSvcDir.Text,
        XflowUseService = XflowSvcRadio.IsChecked == true,
        FexDir        = _fexDir,
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
        FexWorkspace     = FexWorkspace.Text,
        ExtraPrograms    = [.. _extraPrograms],
        ShowXms          = XmsServiceBlock.Visibility  == Visibility.Visible,
        ShowXflow        = XflowServiceBlock.Visibility == Visibility.Visible,
        ShowFex          = FexServiceBlock.Visibility  == Visibility.Visible,
        SettingsExpanded = SettingsPanel.Visibility == Visibility.Visible,
    };

    private void ApplySettings(AppSettings s)
    {
        XmsMapPath.Text   = s.XmsMapPath;
        XmsDbDir.Text     = s.XmsDbDir;
        XflowMapPath.Text = s.XflowMapPath;
        XflowDbDir.Text   = s.XflowDbDir;
        SqlFilePath.Text  = s.SqlFilePath;
        BatDir.Text       = s.BatDir;
        ExeDir.Text       = s.ExeDir;
        XflowSvcDir.Text  = s.XflowSvcDir;
        _fexDir           = s.FexDir;
        // xflow 모드 복원 (RadioButton은 직접 설정)
        if (s.XflowUseService) XflowSvcRadio.IsChecked = true;
        else                   XflowExeRadio.IsChecked  = true;
        ApplyXflowMode(s.XflowUseService);
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
        foreach (var p in s.ExtraPrograms ?? []) _extraPrograms.Add(p);

        SetXmsVisible(s.ShowXms);
        SetXflowVisible(s.ShowXflow);
        SetFexVisible(s.ShowFex);
        // 설정 패널 상태 복원
        SetSettingsPanelVisible(s.SettingsExpanded);
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

        CurrentProfileLabel.Text = string.IsNullOrEmpty(selectedName) ? "—" : selectedName;
    }

    // ── 찾기 버튼들 ─────────────────────────────────────

    /// <summary>파일 선택 대화상자를 현재 경로 위치에서 시작하도록 설정합니다.</summary>
    private static void SeedFileDialog(OpenFileDialog dlg, string current)
    {
        if (string.IsNullOrWhiteSpace(current)) return;
        try
        {
            var dir = Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
            if (File.Exists(current))
                dlg.FileName = Path.GetFileName(current);
        }
        catch { /* 무시 */ }
    }

    /// <summary>폴더 선택 대화상자를 현재 경로 위치에서 시작하도록 설정합니다.</summary>
    private static void SeedFolderDialog(OpenFolderDialog dlg, string current)
    {
        if (string.IsNullOrWhiteSpace(current)) return;
        try
        {
            if (Directory.Exists(current))
                dlg.InitialDirectory = current;
            else
            {
                var parent = Path.GetDirectoryName(current);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                    dlg.InitialDirectory = parent;
            }
        }
        catch { /* 무시 */ }
    }

    private void BrowseXmsMap_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "xms — Map 파일 선택", Filter = "JSON (*.json)|*.json|모든 파일|*.*" };
        SeedFileDialog(dlg, XmsMapPath.Text);
        if (dlg.ShowDialog() != true) return;
        XmsMapPath.Text     = dlg.FileName;
        XmsCmd2Preview.Text = $@".\xms.exe db create ""{dlg.FileName}""";
        SaveSettings();
    }

    private void BrowseXflowMap_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "xflow — Map 파일 선택", Filter = "JSON (*.json)|*.json|모든 파일|*.*" };
        SeedFileDialog(dlg, XflowMapPath.Text);
        if (dlg.ShowDialog() != true) return;
        XflowMapPath.Text     = dlg.FileName;
        XflowCmd2Preview.Text = $@".\xflow.exe db create -m ""{dlg.FileName}""";
        SaveSettings();
    }

    private void BrowseSqlFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "SQL 파일 선택", Filter = "SQL (*.sql)|*.sql|모든 파일|*.*" };
        SeedFileDialog(dlg, SqlFilePath.Text);
        if (dlg.ShowDialog() != true) return;
        SqlFilePath.Text = dlg.FileName;
        SaveSettings();
    }

    private void BrowseBatDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "BAT — 실행 위치 선택" };
        SeedFolderDialog(dlg, BatDir.Text);
        if (dlg.ShowDialog() != true) return;
        BatDir.Text = dlg.FolderName;
        SaveSettings();
    }

    private void BrowseExeDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "xflowd — 실행 위치 선택" };
        SeedFolderDialog(dlg, ExeDir.Text);
        if (dlg.ShowDialog() != true) return;
        ExeDir.Text = dlg.FolderName;
        SaveSettings();
    }

    private void BrowseXflowSvcDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "xflow 서비스 — 실행 위치 선택" };
        SeedFolderDialog(dlg, XflowSvcDir.Text);
        if (dlg.ShowDialog() != true) return;
        XflowSvcDir.Text = dlg.FolderName;
        SaveSettings();
    }

    private void BrowseXmsDbDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "xms DB — 실행 위치 선택" };
        SeedFolderDialog(dlg, XmsDbDir.Text);
        if (dlg.ShowDialog() != true) return;
        XmsDbDir.Text = dlg.FolderName;
        SaveSettings();
    }

    private void BrowseXflowDbDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "xflow DB — 실행 위치 선택" };
        SeedFolderDialog(dlg, XflowDbDir.Text);
        if (dlg.ShowDialog() != true) return;
        XflowDbDir.Text = dlg.FolderName;
        SaveSettings();
    }

    private void XflowMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyXflowMode(XflowSvcRadio.IsChecked == true);
        SaveSettings();
    }

    private void ApplyXflowMode(bool useService)
    {
        XflowSvcPanel.Visibility = useService ? Visibility.Visible   : Visibility.Collapsed;
        XflowExePanel.Visibility = useService ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── 구성요소 표시 / 제거 ──────────────────────────────

    private void SetXmsVisible(bool v)
    {
        XmsServiceBlock.Visibility = v ? Visibility.Visible : Visibility.Collapsed;
        XmsDbGroupBox.Visibility   = v ? Visibility.Visible : Visibility.Collapsed;
        BatStopCheck.Visibility    = v ? Visibility.Visible : Visibility.Collapsed;
        BatStartCheck.Visibility   = v ? Visibility.Visible : Visibility.Collapsed;
        Step2Border.Visibility     = v ? Visibility.Visible : Visibility.Collapsed;
        XmsStatusRow.Visibility    = v ? Visibility.Visible : Visibility.Collapsed;
        RestoreXmsBtn.Visibility   = v ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetXflowVisible(bool v)
    {
        XflowServiceBlock.Visibility = v ? Visibility.Visible : Visibility.Collapsed;
        XflowDbGroupBox.Visibility   = v ? Visibility.Visible : Visibility.Collapsed;
        SqlGroupBox.Visibility       = v ? Visibility.Visible : Visibility.Collapsed;
        ExeStopCheck.Visibility      = v ? Visibility.Visible : Visibility.Collapsed;
        ExeStartCheck.Visibility     = v ? Visibility.Visible : Visibility.Collapsed;
        Step3Border.Visibility       = v ? Visibility.Visible : Visibility.Collapsed;
        Step4Border.Visibility       = v ? Visibility.Visible : Visibility.Collapsed;
        XflowStatusRow.Visibility    = v ? Visibility.Visible : Visibility.Collapsed;
        RestoreXflowBtn.Visibility   = v ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetFexVisible(bool v)
    {
        FexServiceBlock.Visibility = v ? Visibility.Visible : Visibility.Collapsed;
        FexStopCheck.Visibility    = v ? Visibility.Visible : Visibility.Collapsed;
        FexStartCheck.Visibility   = v ? Visibility.Visible : Visibility.Collapsed;
        RestoreFexBtn.Visibility   = v ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RemoveXms_Click(object sender, RoutedEventArgs e)  { SetXmsVisible(false);  SaveSettings(); }
    private void RemoveXflow_Click(object sender, RoutedEventArgs e) { SetXflowVisible(false); SaveSettings(); }
    private void RemoveFex_Click(object sender, RoutedEventArgs e)   { SetFexVisible(false);   SaveSettings(); }
    private void RestoreXms_Click(object sender, RoutedEventArgs e)  { SetXmsVisible(true);   SaveSettings(); }
    private void RestoreXflow_Click(object sender, RoutedEventArgs e){ SetXflowVisible(true);  SaveSettings(); }
    private void RestoreFex_Click(object sender, RoutedEventArgs e)  { SetFexVisible(true);    SaveSettings(); }



    // ── FEX 워크스페이스 자동화 ──────────────────────────────

    private async Task AutomateFexStartupAsync(Process fexProcess, string workspace)
    {
        var target = string.IsNullOrWhiteSpace(workspace) ? "Default" : workspace;
        Log($"  FEX 창 대기 중... (워크스페이스: {target})");

        // ── Step 1: FEX 창 찾기 (업데이트 후 재시작 포함, 최대 60초) ──
        AutomationElement? fexWindow = null;
        int targetPid = fexProcess.Id;

        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(1000);

            // 원본 프로세스가 종료됐으면 새 Fex Hub 프로세스로 전환
            if (fexProcess.HasExited)
            {
                var newProcs = Process.GetProcessesByName("Fex Hub");
                if (newProcs.Length > 0 && newProcs[0].Id != fexProcess.Id)
                {
                    if (targetPid == fexProcess.Id)   // 첫 전환 시에만 로그
                        Log($"  ℹ 업데이트 감지 — 새 프로세스(PID {newProcs[0].Id}) 대기 중...");
                    targetPid = newProcs[0].Id;
                }
                else continue;
            }

            try
            {
                fexWindow = AutomationElement.RootElement.FindFirst(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, targetPid));
                if (fexWindow != null) break;
            }
            catch { /* 재시도 */ }
        }

        if (fexWindow == null)
        {
            Log("  ⚠ FEX 창을 찾지 못했습니다 (수동으로 워크스페이스를 선택하세요)");
            return;
        }

        // ── Step 2: Start 버튼 활성화(Ready) 대기 ──────────────────────
        // Downloading → Download Complete → Ready 순서로 진행됨
        // Start 버튼이 enabled 되면 ComboBox도 사용 가능
        Log("  ⏳ FEX Ready 대기 중...");
        AutomationElement? startBtn = null;

        for (int i = 0; i < 120; i++)
        {
            await Task.Delay(1000);
            try
            {
                // 프로세스 재시작 시 창 참조 갱신
                var latest = Process.GetProcessesByName("Fex Hub");
                if (latest.Length > 0 && latest[0].Id != targetPid)
                {
                    targetPid = latest[0].Id;
                    fexWindow = null;
                }
                fexWindow ??= AutomationElement.RootElement.FindFirst(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, targetPid));
                if (fexWindow == null) continue;

                startBtn = fexWindow.FindFirst(TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                        new PropertyCondition(AutomationElement.NameProperty, "Start")));

                if (startBtn != null &&
                    (bool)startBtn.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty))
                {
                    Log("  ✓ FEX Ready");
                    break;
                }
                startBtn = null;
            }
            catch { fexWindow = null; /* 창 재탐색 */ }

            if (i > 0 && i % 15 == 0)
                Log($"  ⏳ {i}초 경과...");
        }

        if (startBtn == null)
        {
            Log("  ⚠ Start 버튼이 활성화되지 않았습니다 (수동으로 눌러주세요)");
            return;
        }

        // ── Step 3: 워크스페이스 선택 ──────────────────────────────────
        try
        {
            var combo = fexWindow!.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox));

            if (combo == null)
            {
                Log("  ⚠ 워크스페이스 목록을 찾지 못했습니다");
            }
            else
            {
                if (combo.GetCurrentPattern(ExpandCollapsePattern.Pattern) is ExpandCollapsePattern expand)
                {
                    expand.Expand();
                    await Task.Delay(300);
                }

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
        }
        catch (Exception ex) { Log($"  ⚠ 워크스페이스 선택 실패: {ex.Message}"); }

        // ── Step 4: Start 클릭 ─────────────────────────────────────────
        try
        {
            if (startBtn.GetCurrentPattern(InvokePattern.Pattern) is InvokePattern invoke)
            {
                await Task.Delay(200);
                invoke.Invoke();
                Log("  ✓ FEX Start 완료");
            }
            else Log("  ⚠ Start 버튼을 클릭할 수 없습니다 (수동으로 눌러주세요)");
        }
        catch (Exception ex) { Log($"  ⚠ Start 버튼 클릭 실패: {ex.Message}"); }
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
        SeedFileDialog(dlg, item.ExePath);
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
        CurrentProfileLabel.Text = name;
        Log($"프로필 불러옴: {name}");
    }

    // ── 실행 — 전체 / 개별 ───────────────────────────────

    private bool _isRunning;

    /// UI를 잠그고 action을 실행한 뒤 복원합니다.
    private async Task RunWithLock(Func<Task> action)
    {
        _isRunning               = true;
        ExecuteBtn.IsEnabled     = false;
        ExecuteBtn.Content       = "⏳  실행 중...";
        SetAllRunBtns(false);
        try   { await action(); }
        finally
        {
            _isRunning           = false;
            ExecuteBtn.IsEnabled = true;
            ExecuteBtn.Content   = "▶  실행";
            SetAllRunBtns(true);
        }
    }

    private void SetAllRunBtns(bool enabled)
    {
        RunStep1Btn.IsEnabled = RunStep2Btn.IsEnabled = RunStep3Btn.IsEnabled =
        RunStep4Btn.IsEnabled = RunStep5Btn.IsEnabled = enabled;
    }

    // ── 전체 실행 버튼 ────────────────────────────────────

    private async void ExecuteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        await RunWithLock(async () =>
        {
            LogBox.Clear();
            ResetAllSteps();
            var cmd = new CommandRunner(Log);
            var db  = new DbInitializer(Log);
            var prg = new ProcessLauncher(Log);
            await Step1Async(cmd, prg);
            await Step2Async(cmd);
            await Step3Async(cmd);
            await Step4Async(db);
            await Step5Async(cmd, prg);
            Log("════ ✅ 완료 ════");
            StartPolling();
        });
    }

    // ── 개별 실행 버튼 ────────────────────────────────────

    private async void RunStep1_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        await RunWithLock(async () =>
        {
            SetStep(1, StepState.Inactive);
            await Step1Async(new CommandRunner(Log), new ProcessLauncher(Log));
        });
    }

    private async void RunStep2_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        await RunWithLock(async () =>
        {
            SetStep(2, StepState.Inactive);
            await Step2Async(new CommandRunner(Log));
        });
    }

    private async void RunStep3_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        await RunWithLock(async () =>
        {
            SetStep(3, StepState.Inactive);
            await Step3Async(new CommandRunner(Log));
        });
    }

    private async void RunStep4_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        await RunWithLock(async () =>
        {
            SetStep(4, StepState.Inactive);
            await Step4Async(new DbInitializer(Log));
        });
    }

    private async void RunStep5_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        await RunWithLock(async () =>
        {
            SetStep(5, StepState.Inactive);
            await Step5Async(new CommandRunner(Log), new ProcessLauncher(Log));
            StartPolling();
        });
    }

    // ── 단계별 실행 로직 ─────────────────────────────────

    private async Task Step1Async(CommandRunner cmd, ProcessLauncher prg)
    {
        SetStep(1, StepState.Active);
        Log("════ ① 종료 단계 ════");
        var svc = new ServiceStatusChecker(Log);

        if (BatStopCheck.IsChecked == true && !string.IsNullOrWhiteSpace(BatDir.Text))
        {
            try
            {
                Log("  xms service stop (최대 30초 대기)");
                await cmd.RunAsync(BatDir.Text, @".\xms.exe service stop", timeoutMs: 30_000);
                await svc.LogStatusAsync("xms", BatDir.Text, "xms.exe");
            }
            catch (Exception ex) { Log($"  ⚠ xms service stop 건너뜀: {ex.Message}"); }
        }
        else Log("  xms 서비스 — 건너뜀");

        if (ExeStopCheck.IsChecked == true)
        {
            try
            {
                if (XflowSvcRadio.IsChecked == true && !string.IsNullOrWhiteSpace(XflowSvcDir.Text))
                {
                    Log("  xflow service stop (최대 30초 대기)");
                    await cmd.RunAsync(XflowSvcDir.Text, @".\xflow.exe service stop", timeoutMs: 30_000);
                    await svc.LogStatusAsync("xflow", XflowSvcDir.Text, "xflow.exe");
                }
                else
                {
                    Log("  xflowd.exe 프로세스 종료");
                    await prg.KillProcessByNameAsync("xflowd.exe");
                }
            }
            catch (Exception ex) { Log($"  ⚠ xflow 건너뜀: {ex.Message}"); }
        }
        else Log("  xflow — 건너뜀");

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

        foreach (var p in _extraPrograms)
        {
            var label = string.IsNullOrWhiteSpace(p.DisplayName) ? "(이름 없음)" : p.DisplayName;
            if (!p.StopEnabled)                          { Log($"  {label} — 건너뜀"); continue; }
            if (string.IsNullOrWhiteSpace(p.ExePath))   { Log($"  {label} — 실행 파일 미설정, 건너뜀"); continue; }
            try
            {
                Log($"  {label} 종료");
                await prg.KillProcessByNameAsync(p.FileName);
            }
            catch (Exception ex) { Log($"  ⚠ {label} 건너뜀: {ex.Message}"); }
        }

        SetStep(1, StepState.Done);
    }

    private async Task Step2Async(CommandRunner cmd)
    {
        SetStep(2, StepState.Active);
        Log("════ ② xms DB ════");
        if (XmsDbCheck.IsChecked != true)
        {
            Log("  ○ 건너뜀");
            SetStep(2, StepState.Skipped);
            return;
        }
        var xmsDbDir = XmsDbDir.Text.Trim();
        if (string.IsNullOrWhiteSpace(xmsDbDir))
        {
            Log("  ⚠ 실행 위치 미설정 — 건너뜀");
            SetStep(2, StepState.Skipped);
            return;
        }
        try
        {
            Log("  db drop -a");
            await cmd.RunAsync(xmsDbDir, @".\xms.exe db drop -a");
            if (!string.IsNullOrWhiteSpace(XmsMapPath.Text))
            {
                Log("  db create");
                await cmd.RunAsync(xmsDbDir, $@".\xms.exe db create ""{XmsMapPath.Text}""");
            }
            else Log("  Map 미설정 — db create 건너뜀");
            SetStep(2, StepState.Done);
        }
        catch (Exception ex) { SetStep(2, StepState.Error); Log($"  ❌ {ex.Message}"); }
    }

    private async Task Step3Async(CommandRunner cmd)
    {
        SetStep(3, StepState.Active);
        Log("════ ③ xflow DB ════");
        if (XflowDbCheck.IsChecked != true)
        {
            Log("  ○ 건너뜀");
            SetStep(3, StepState.Skipped);
            return;
        }
        var xflowDbDir = XflowDbDir.Text.Trim();
        if (string.IsNullOrWhiteSpace(xflowDbDir))
        {
            Log("  ⚠ 실행 위치 미설정 — 건너뜀");
            SetStep(3, StepState.Skipped);
            return;
        }
        try
        {
            Log("  db admin drop -a");
            await cmd.RunAsync(xflowDbDir, @".\xflow.exe db admin drop -a");
            if (!string.IsNullOrWhiteSpace(XflowMapPath.Text))
            {
                Log("  db create");
                await cmd.RunAsync(xflowDbDir, $@".\xflow.exe db create -m ""{XflowMapPath.Text}""");
            }
            else Log("  Map 미설정 — db create 건너뜀");
            SetStep(3, StepState.Done);
        }
        catch (Exception ex) { SetStep(3, StepState.Error); Log($"  ❌ {ex.Message}"); }
    }

    private async Task Step4Async(DbInitializer _)
    {
        SetStep(4, StepState.Active);
        Log("════ ④ PostgreSQL SQL ════");
        if (SqlCheck.IsChecked != true)
        {
            Log("  ○ 건너뜀");
            SetStep(4, StepState.Skipped);
            return;
        }
        try
        {
            if (!string.IsNullOrWhiteSpace(SqlFilePath.Text))
            {
                var sqlWin = new SqlLogWindow();
                sqlWin.Show();
                var sqlDb = new DbInitializer(sqlWin.AppendLog);
                Log("  SQL 실행 중... (별도 창에서 출력)");
                await sqlDb.RunSqlFileAsync(DbHost.Text, DbPort.Text, DbName.Text,
                                            DbUser.Text, DbPassword.Password, SqlFilePath.Text);
                Log("  ✓ SQL 완료");
                sqlWin.Close();
            }
            else
                Log("  SQL 파일 미설정, 건너뜀");
            SetStep(4, StepState.Done);
        }
        catch (Exception ex) { SetStep(4, StepState.Error); Log($"  ❌ {ex.Message}"); }
    }

    private async Task Step5Async(CommandRunner cmd, ProcessLauncher prg)
    {
        SetStep(5, StepState.Active);
        Log("════ ⑤ 시작 단계 ════");
        var svc = new ServiceStatusChecker(Log);
        try
        {
            if (BatStartCheck.IsChecked == true && !string.IsNullOrWhiteSpace(BatDir.Text))
            {
                Log("  xms service start");
                await cmd.RunAsync(BatDir.Text, @".\xms.exe service start", timeoutMs: 30_000);
                await svc.LogStatusAsync("xms", BatDir.Text, "xms.exe");
            }
            else Log("  xms 서비스 — 건너뜀");

            if (ExeStartCheck.IsChecked == true)
            {
                if (XflowSvcRadio.IsChecked == true && !string.IsNullOrWhiteSpace(XflowSvcDir.Text))
                {
                    Log("  xflow service start");
                    await cmd.RunAsync(XflowSvcDir.Text, @".\xflow.exe service start", timeoutMs: 30_000);
                    await svc.LogStatusAsync("xflow", XflowSvcDir.Text, "xflow.exe");
                }
                else if (XflowExeRadio.IsChecked == true && !string.IsNullOrWhiteSpace(ExeDir.Text))
                {
                    Log("  xflowd.exe 실행");
                    await prg.StartInDirAsync(ExeDir.Text, "xflowd.exe");
                }
                else Log("  xflow — 경로 미설정, 건너뜀");
            }
            else Log("  xflow — 건너뜀");

            if (FexStartCheck.IsChecked == true && !string.IsNullOrWhiteSpace(_fexDir))
            {
                // 이미 실행 중이면 스킵
                var existing = Process.GetProcessesByName("Fex Hub");
                if (existing.Length > 0)
                {
                    Log($"  ℹ Fex Hub 이미 실행 중 (PID {existing[0].Id}) — 건너뜀");
                }
                else
                {
                    Log("  Fex Hub.exe 실행");
                    var fexProcess = Process.Start(new ProcessStartInfo(
                        Path.Combine(_fexDir, "Fex Hub.exe"))
                    {
                        UseShellExecute  = true,
                        WorkingDirectory = _fexDir
                    });
                    if (fexProcess != null)
                    {
                        await AutomateFexStartupAsync(fexProcess, FexWorkspace.Text.Trim());
                        // 업데이트 후 재시작 대비 — 원본 PID 대신 프로세스명으로 최종 확인
                        await Task.Delay(2000);
                        var fexProcs = Process.GetProcessesByName("Fex Hub");
                        if (fexProcs.Length > 0)
                            Log($"  ✓ Fex Hub 실행 중 (PID {fexProcs[0].Id})");
                        else if (!fexProcess.HasExited)
                            Log($"  ✓ Fex Hub.exe 실행 중 (PID {fexProcess.Id})");
                        else
                            Log($"  ⚠ Fex Hub.exe 종료됨 — 업데이트 중이면 잠시 후 자동 재시작됩니다");
                    }
                }
            }
            else Log("  FEX — 건너뜀");

            foreach (var p in _extraPrograms)
            {
                var label = string.IsNullOrWhiteSpace(p.DisplayName) ? "(이름 없음)" : p.DisplayName;
                if (!p.StartEnabled)                         { Log($"  {label} — 건너뜀"); continue; }
                if (string.IsNullOrWhiteSpace(p.ExePath))   { Log($"  {label} — 실행 파일 미설정, 건너뜀"); continue; }
                Log($"  {label} 실행");
                await prg.StartInDirAsync(Path.GetDirectoryName(p.ExePath) ?? "", p.FileName, p.ShowWindow);
            }

            SetStep(5, StepState.Done);
        }
        catch (Exception ex) { SetStep(5, StepState.Error); Log($"  ❌ {ex.Message}"); }
    }

    // ── 단계 상태 관리 ─────────────────────────────────────

    private enum StepState { Inactive, Active, Done, Error, Skipped }

    // Active (실행 중) — 전 단계 앰버 통일
    private static readonly (string Bg, string Bd, string Fg)[] ActiveColors =
    [
        default,
        ("#FFF8E1", "#FFC107", "#E65100"),
        ("#FFF8E1", "#FFC107", "#E65100"),
        ("#FFF8E1", "#FFC107", "#E65100"),
        ("#FFF8E1", "#FFC107", "#E65100"),
        ("#FFF8E1", "#FFC107", "#E65100"),
    ];

    // Done (완료) — 전 단계 다크 통일
    private static readonly (string Bg, string Bd, string Fg)[] DoneColors =
    [
        default,
        ("#F8F9FA", "#1E1E1E", "#1E1E1E"),
        ("#F8F9FA", "#1E1E1E", "#1E1E1E"),
        ("#F8F9FA", "#1E1E1E", "#1E1E1E"),
        ("#F8F9FA", "#1E1E1E", "#1E1E1E"),
        ("#F8F9FA", "#1E1E1E", "#1E1E1E"),
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
                StepState.Done     => DoneColors[step],
                StepState.Error    => ("#FFEBEE", "#E53935", "#C62828"),
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
        // 숨겨진(제거된) 구성요소 체크박스는 집계에서 제외
        var visible = new[] {
            BatStopCheck,  ExeStopCheck,  FexStopCheck,
            XmsDbCheck,    XflowDbCheck,  SqlCheck,
            BatStartCheck, ExeStartCheck, FexStartCheck,
        }.Where(cb => cb.Visibility == Visibility.Visible).ToArray();

        bool?[] states = visible.Select(cb => cb.IsChecked).ToArray();
        bool allOn  = states.All(s => s == true);
        bool allOff = states.All(s => s != true);

        _suppressSelectAll = true;
        SelectAllCheck.IsChecked = allOn ? true : allOff ? false : null;  // null = indeterminate
        _suppressSelectAll = false;
    }

    // ── 로그 헬퍼 ─────────────────────────────────────────

    private void LogSave_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LogBox.Text)) return;
        try
        {
            var dir  = Path.Combine(AppDir, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, LogBox.Text);
            Log($"  💾 로그 저장됨: {path}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"로그 저장 실패:\n{ex.Message}", "오류",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

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
