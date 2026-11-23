using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TeamRadio56.Core.Config;
using TeamRadio56.Core.Diagnostics;
using TeamRadio56.Core.Logic;
using TeamRadio56.Core.Telemetry;
using TeamRadio56.Core.Update;

namespace TeamRadio56.SimHub
{
    /// <summary>
    /// SimHub 좌측 메뉴에 붙는 설정 화면.
    ///
    /// XAML 없이 코드로 조립한다 — x:Class 매칭이나 .g.cs 생성 같은
    /// XAML 빌드 실패 지점을 없애기 위함 (개발 환경에서 컴파일 검증 불가).
    /// 문자열은 전부 Loc(ko/en)을 거친다 — 화면 언어를 바꾸면 즉시 재조립.
    /// 항목 설명은 늘 보이는 회색 글씨 대신 마우스를 올렸을 때 툴팁으로 (ⓘ 표시가 있는 곳).
    /// 상태·경고·눌러야 할 안내는 그대로 보인다.
    /// </summary>
    public class SettingsControl : UserControl
    {
        private static readonly Brush Fg = Brushes.Gainsboro;
        private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x90, 0x96, 0x9C));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
        private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84));
        private static readonly Brush Off = new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73));
        // 툴팁을 단 패널의 배경. 배경이 없는(null) 패널은 자식 위에서만 마우스가 걸려, 라벨 칸의 빈 곳·
        // 글자와 ⓘ 사이 여백·라벨과 컨트롤 사이를 지날 때 툴팁이 닫혔다가 새 자리에서 다시 뜬다
        // (깜빡이며 튄다). 투명으로라도 칠하면 패널 전체가 툴팁 영역이 된다
        private static readonly Brush HitArea = Brushes.Transparent;

        // 툴팁 — 기본 표시 시간 5초는 문단 하나 읽기에 짧다. 폭은 긴 설명이 화면 끝까지
        // 늘어지지 않게 줄바꿈하는 기준
        private const int TipShowMs = 60000;
        private const int TipDelayMs = 400;
        private const double TipMaxWidth = 420;

        private readonly TeamRadio56Plugin _plugin;
        // 화면을 다시 그릴 때마다 새로 만든다 — WPF는 이미 다른 패널에 붙은
        // 요소를 새 패널에 붙이면 예외를 던져, 재조립(언어 전환)이 실패한다
        private TextBlock _connection = new TextBlock();
        private TextBlock _status = new TextBlock();
        private TextBlock _source = new TextBlock();
        private TextBlock _recent = new TextBlock();
        private TextBlock _engineState = new TextBlock();
        // 엔진 상태 블록(글 + ⓘ)과 그 툴팁(음성 팩 폴더 경로) — 매초 갱신이라 글이 바뀔 때만 손댄다
        private DockPanel _engineInfo = new DockPanel();
        private Border _engineMark = new Border();
        private string _engineTip;
        // [엔진 재시작] 옆 "설정이 바뀌었습니다" — 엔진이 아직 안 읽은 변경이 있을 때만 보인다
        private TextBlock _enginePending = new TextBlock();
        private TextBlock _testSpeak = new TextBlock();
        private readonly DispatcherTimer _timer = new DispatcherTimer();

        // 업데이트 섹션 — Build마다 새로 만든다 (상태는 플러그인의 UpdateController에)
        private TextBlock _updBanner;
        private TextBlock _updVersions;
        private TextBlock _updAudio;
        private TextBlock _updStatus;
        private Button _btnUpdCheck;
        private Button _btnUpdPlugin;
        private Button _btnUpdAudio;
        private Button _btnUpdCancel;
        private int _updRefreshQueued;

        public SettingsControl(TeamRadio56Plugin plugin)
        {
            _plugin = plugin;
            Loc.Lang = (S != null ? S.UiLanguage : null) ?? "ko";
            Build();

            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (s, e) => Refresh();
            Loaded += (s, e) =>
            {
                SubscribeUpdater(true);
                Refresh();
                _timer.Start();
            };
            Unloaded += (s, e) =>
            {
                _timer.Stop();
                SubscribeUpdater(false);
                // 디바운스 중이던 저장이 있으면 화면을 떠날 때 마저 쓴다
                if (_saveTimer != null && _saveTimer.IsEnabled)
                {
                    _saveTimer.Stop();
                    _plugin.SaveSettings();
                }
            };
        }

        private PluginSettings S { get { return _plugin.Settings; } }

        private static string L(string key)
        {
            return Loc.L(key);
        }

        /// <summary>콤보 표시명: "{prefix}{값}" Loc 키가 있으면 그걸, 없으면 값 그대로.</summary>
        private static Func<string, string> Choice(string prefix)
        {
            return v =>
            {
                string key = prefix + v;
                string text = Loc.L(key);
                return text == key ? v : text;
            };
        }

        private DispatcherTimer _saveTimer;

        /// <summary>
        /// 저장 디바운스 — 슬라이더 드래그 한 번에 수십 번 파일을 쓰면
        /// UI가 버벅인다. 마지막 변경 후 잠깐 쉬면 한 번만 저장.
        /// </summary>
        private void Save()
        {
            if (_saveTimer == null)
            {
                _saveTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(600),
                };
                _saveTimer.Tick += (s, e) =>
                {
                    _saveTimer.Stop();
                    _plugin.SaveSettings();
                };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        // -- 화면 구성 -------------------------------------------------------

        private void Build()
        {
            var root = new StackPanel { Margin = new Thickness(14, 10, 14, 20) };

            var header = new StackPanel { Orientation = Orientation.Horizontal };
            // 제목 + ⓘ — 무엇을 하는 플러그인인지는 호버로, 늘 보이는 줄은 버전만.
            // 투명 배경: 글자와 ⓘ 사이 여백도 툴팁 영역이 되게 (HitArea 주석 참고)
            var title = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Background = HitArea,
            };
            var titleText = new TextBlock
            {
                Text = "teamradio56",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = Accent,
            };
            title.Children.Add(titleText);
            title.Children.Add(InfoMark());
            Tip(title, L("app_hint"), titleText);
            header.Children.Add(title);
            // 화면 언어 전환 — 헤더 우측, 바꾸면 화면을 즉시 다시 그린다
            var langCombo = MakeCombo(PluginSettings.UiLanguageChoices,
                S != null ? S.UiLanguage : "ko", v =>
                {
                    if (S != null)
                        S.UiLanguage = v;
                    Loc.Lang = v;
                    RebuildLater();   // 콤보 이벤트가 끝난 뒤 새 언어로 재조립
                }, v => v == "ko" ? "한국어" : "English");
            ((FrameworkElement)langCombo).Width = 100;
            ((FrameworkElement)langCombo).Margin = new Thickness(16, 4, 0, 0);
            header.Children.Add(langCombo);
            root.Children.Add(header);

            root.Children.Add(new TextBlock
            {
                Text = string.Format(L("version"), TeamRadio56Plugin.Version),
                Foreground = Dim,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 4),
            });
            _updBanner = new TextBlock
            {
                Foreground = Accent,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 2, 0, 2),
            };
            root.Children.Add(_updBanner);

            BuildStatus(root);
            BuildEngine(root);
            BuildVoice(root);
            BuildChatter(root);
            BuildTraffic(root);
            BuildReports(root);
            BuildBehaviour(root);
            BuildUpdate(root);
            RefreshUpdate();

            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = root,
            };
            if (IsLoaded)
                Refresh();   // 새 상태 표시가 다음 타이머 틱까지 비어 있지 않게
        }

        private void BuildStatus(StackPanel root)
        {
            StackPanel box = Section(root, L("sec_status"));

            _connection = new TextBlock();
            _status = new TextBlock();
            _recent = new TextBlock();
            _connection.Foreground = Dim;
            _connection.FontWeight = FontWeights.Bold;
            box.Children.Add(_connection);

            // 어떤 공유 메모리로 읽는지 (LMU 내장 / rF2 DLL), 연결이 없으면 무엇을 켜야 하는지
            _source = new TextBlock();
            _source.Foreground = Dim;
            _source.TextWrapping = TextWrapping.Wrap;
            box.Children.Add(_source);

            _status.Foreground = Fg;
            _status.TextWrapping = TextWrapping.Wrap;
            _status.Margin = new Thickness(0, 2, 0, 0);
            box.Children.Add(_status);

            _recent.Foreground = Dim;
            _recent.TextWrapping = TextWrapping.Wrap;
            _recent.Margin = new Thickness(0, 6, 0, 0);
            box.Children.Add(_recent);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 0),
            };
            buttons.Children.Add(MakeButton(L("btn_test_speak"), () => _plugin.TestSpeak()));
            buttons.Children.Add(MakeButton(L("btn_open_log"), () => OpenFile(FileLog.Path)));
            buttons.Children.Add(MakeButton(L("btn_open_settings"), () => OpenFile(SettingsStore.Path)));
            box.Children.Add(buttons);

            _testSpeak = new TextBlock();
            _testSpeak.Foreground = Dim;
            _testSpeak.TextWrapping = TextWrapping.Wrap;
            _testSpeak.Margin = new Thickness(0, 4, 0, 0);
            box.Children.Add(_testSpeak);
        }

        private void BuildEngine(StackPanel root)
        {
            // 엔진 설명과 "바꾸면 [엔진 재시작]" 안내는 섹션 제목의 툴팁으로
            StackPanel box = Section(root, L("sec_engine"),
                L("engine_hint") + "\n\n" + L("engine_apply_hint"));

            // 상태 블록 — 엔진·효과본 수·설치된 팩·경고는 늘 보이고, 음성 팩 폴더 경로만
            // 블록의 툴팁(ⓘ)으로. ⓘ는 글 오른쪽 위에 붙인다 (블록이 글 폭만큼만 차지하게 Left)
            _engineState = new TextBlock();
            _engineState.Foreground = Dim;
            _engineState.TextWrapping = TextWrapping.Wrap;
            _engineMark = InfoMark();
            _engineMark.VerticalAlignment = VerticalAlignment.Top;
            _engineMark.Margin = new Thickness(6, 1, 0, 0);
            _engineMark.Visibility = Visibility.Collapsed;   // 경로를 알 때 Refresh가 켠다
            DockPanel.SetDock(_engineMark, Dock.Right);
            _engineInfo = new DockPanel
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = HitArea,   // 글과 ⓘ 사이 여백에서도 툴팁이 닫히지 않게
                Margin = new Thickness(0, 2, 0, 6),
            };
            _engineInfo.Children.Add(_engineMark);
            _engineInfo.Children.Add(_engineState);
            _engineTip = null;   // 새 블록엔 툴팁이 없다 — 다음 Refresh가 다시 단다
            box.Children.Add(_engineInfo);

            // 음성 팩(오디오 캐시) 폴더 — 비우면 자동 탐색, [음성 팩 업데이트]도 같은 폴더에 설치
            Row(box, L("row_cache_dir"), MakeText(S.AudioCacheDir, v =>
            {
                S.AudioCacheDir = v;
            }), L("hint_cache_dir"));

            // 버튼 + 오른쪽에 "설정이 바뀌었습니다" 안내 — 같은 줄이라 높이를 더 쓰지 않는다 (좁으면 안내만 줄바꿈)
            var buttons = new DockPanel
            {
                LastChildFill = true,
                Margin = new Thickness(0, 6, 0, 0),
            };
            // 재시작은 보이스 워커 정지(최대 10초)를 기다린다 — UI 스레드 밖에서
            var restart = (Button)MakeButton(L("btn_engine_restart"),
                () => InBackground(_plugin.RestartEngine));
            restart.VerticalAlignment = VerticalAlignment.Center;
            Tip(restart, L("hint_engine_restart"));
            DockPanel.SetDock(restart, Dock.Left);
            buttons.Children.Add(restart);
            // 엔진이 시작할 때만 읽는 설정을 바꿨으면 눌러야 반영된다 — 사용자가 해야 할 일이라
            // 호버로 숨기지 않고, 아직 반영 안 된 변경이 있을 때만 Refresh가 켠다
            _enginePending = new TextBlock
            {
                Text = L("engine_apply_note"),
                Foreground = Accent,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            buttons.Children.Add(_enginePending);
            box.Children.Add(buttons);
        }

        private void BuildVoice(StackPanel root)
        {
            StackPanel box = Section(root, L("sec_voice"));

            // 목소리는 kokoro 음성 팩 하나뿐 — 고를 것은 출력 on/off뿐이다
            Row(box, L("row_voice_on"), MakeCheck(S.VoiceEnabled, v =>
            {
                S.VoiceEnabled = v;
            }), L("hint_voice_on"));
        }

        private void BuildChatter(StackPanel root)
        {
            StackPanel box = Section(root, L("sec_chatter"), L("chatter_hint"));
            Row(box, L("row_preset"), MakeCombo(PluginSettings.ChatterChoices, S.ChatterPreset, v =>
            {
                S.ChatterPreset = v;
            }, Choice("choice_")), L("hint_preset"));
        }

        private void BuildTraffic(StackPanel root)
        {
            StackPanel box = Section(root, L("sec_traffic"));

            Row(box, L("row_alongside"), MakeSlider(3.0, 10.0, 0.2, S.AlongsideMeters, v =>
            {
                S.AlongsideMeters = v;
            }, "{0:0.0} m"), L("hint_alongside"));

            Row(box, L("row_spotter"), MakeSlider(0, 120, 5, S.StartSpotterSeconds, v =>
            {
                S.StartSpotterSeconds = v;
            }, L("fmt_seconds")), L("hint_spotter"));

            Row(box, L("row_invert"), MakeCheck(S.SideInvert, v =>
            {
                S.SideInvert = v;
            }), L("hint_invert"));

            Row(box, L("row_race_only"), MakeCheck(S.TrafficRaceOnly, v =>
            {
                S.TrafficRaceOnly = v;
            }), L("hint_race_only"));
        }

        private void BuildReports(StackPanel root)
        {
            StackPanel box = Section(root, L("sec_reports"), L("reports_hint"));

            Row(box, L("row_laptime"), MakeCheck(S.LapTimeEveryLap, v =>
            {
                S.LapTimeEveryLap = v;
            }), L("hint_laptime"));

            Row(box, L("row_status_report"), MakeSlider(0, 10, 1, S.StatusEveryLaps, v =>
            {
                S.StatusEveryLaps = (int)Math.Round(v);
            }, L("fmt_every_laps")), L("hint_status_report"));
        }

        private void BuildBehaviour(StackPanel root)
        {
            StackPanel box = Section(root, L("sec_behaviour"));

            Row(box, L("row_realtime"), MakeCheck(S.RequireRealtime, v =>
            {
                S.RequireRealtime = v;
            }), L("hint_realtime"));
        }


        private void BuildUpdate(StackPanel root)
        {
            StackPanel box = Section(root, L("sec_update"), L("update_hint"));

            _updVersions = new TextBlock { Foreground = Fg, TextWrapping = TextWrapping.Wrap };
            box.Children.Add(_updVersions);
            _updAudio = new TextBlock
            {
                Foreground = Fg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            };
            box.Children.Add(_updAudio);
            _updStatus = new TextBlock
            {
                Foreground = Dim,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            };
            box.Children.Add(_updStatus);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 4),
            };
            _btnUpdCheck = (Button)MakeButton(L("btn_upd_check"), () =>
            {
                UpdateController u = _plugin.Updater;
                if (u != null)
                    u.StartCheck();
                RefreshUpdate();
            });
            _btnUpdPlugin = (Button)MakeButton(L("btn_upd_plugin"), ConfirmInstallPlugin);
            _btnUpdAudio = (Button)MakeButton(L("btn_upd_audio"), ConfirmInstallAudio);
            _btnUpdCancel = (Button)MakeButton(L("btn_upd_cancel"), () =>
            {
                UpdateController u = _plugin.Updater;
                if (u != null)
                    u.Cancel();
            });
            buttons.Children.Add(_btnUpdCheck);
            buttons.Children.Add(_btnUpdPlugin);
            buttons.Children.Add(_btnUpdAudio);
            buttons.Children.Add(_btnUpdCancel);
            buttons.Children.Add(MakeButton(L("btn_upd_releases"),
                () => OpenFile(UpdateService.DefaultReleasesUrl)));
            box.Children.Add(buttons);

            Row(box, L("row_check_updates"), MakeCheck(S.CheckUpdates, v =>
            {
                S.CheckUpdates = v;
            }), L("hint_check_updates"));
        }

        private void ConfirmInstallPlugin()
        {
            UpdateController u = _plugin.Updater;
            UpdateState st = u != null ? u.State : null;
            if (st == null || !st.CanInstallPlugin)
                return;
            string text = string.Format(L("upd_confirm_plugin"),
                TeamRadio56Plugin.Version, st.Check.Latest.Version);
            if (Confirm(text))
                u.StartInstallPlugin();
            RefreshUpdate();
        }

        private void ConfirmInstallAudio()
        {
            UpdateController u = _plugin.Updater;
            UpdateState st = u != null ? u.State : null;
            if (st == null || !st.CanInstallAudio)
                return;
            long size = st.Check.Latest.AudioTotalSize;
            string text = string.Format(L("upd_confirm_audio"), st.Check.Latest.AudioPack,
                size > 0 ? string.Format(L("upd_confirm_size"), FormatBytes(size)) : "");
            if (Confirm(text))
                u.StartInstallAudio();
            RefreshUpdate();
        }

        private bool Confirm(string text)
        {
            Window owner = Window.GetWindow(this);
            MessageBoxResult r = owner != null
                ? MessageBox.Show(owner, text, L("upd_confirm_title"),
                                  MessageBoxButton.YesNo, MessageBoxImage.Question)
                : MessageBox.Show(text, L("upd_confirm_title"),
                                  MessageBoxButton.YesNo, MessageBoxImage.Question);
            return r == MessageBoxResult.Yes;
        }

        // -- 상태 갱신 -------------------------------------------------------

        /// <summary>
        /// 엔진 재시작은 보이스 워커 정지(최대 10초)를 기다린다 — UI 스레드에서
        /// 돌리면 SimHub 화면이 언다.
        /// </summary>
        private void InBackground(Action work)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    FileLog.Error("엔진 작업 실패", ex);
                }
                try
                {
                    Dispatcher.BeginInvoke(new Action(Refresh));
                }
                catch (Exception) { }
            });
        }

        /// <summary>
        /// 콤보 이벤트 처리 중에 Content를 갈아끼우면 열려 있던 드랍다운이
        /// 고장난다(선택이 씹히거나 팝업이 남음) — 이벤트가 끝난 뒤로 미룬다.
        /// </summary>
        private void RebuildLater()
        {
            // Background 우선순위는 렌더링이 바쁘면 밀릴 수 있다 — Normal로
            Dispatcher.BeginInvoke(new Action(Build), DispatcherPriority.Normal);
        }

        /// <summary>같은 값 재대입으로 매초 레이아웃이 출렁이지 않게.</summary>
        private static void SetText(TextBlock block, string text, Brush brush)
        {
            if (!string.Equals(block.Text, text, StringComparison.Ordinal))
                block.Text = text;
            if (!ReferenceEquals(block.Foreground, brush))
                block.Foreground = brush;
        }

        private static void SetVisible(UIElement el, bool visible)
        {
            Visibility v = visible ? Visibility.Visible : Visibility.Collapsed;
            if (el.Visibility != v)
                el.Visibility = v;
        }

        private void Refresh()
        {
            try
            {
                bool connected = _plugin.IsConnected;
                SetText(_connection, connected ? L("conn_on") : L("conn_off"),
                        connected ? Ok : Off);
                bool sourceWarn;
                SetText(_source, SourceLine(out sourceWarn), sourceWarn ? Off : Dim);
                SetText(_status, _plugin.StatusText ?? "", Fg);

                bool voiceWarn;
                string voiceLines = VoiceLines(out voiceWarn);
                string cacheDir = _plugin.BuiltinCacheDir;
                string cache = cacheDir == null
                    ? L("cache_unknown")
                    : string.Format(L("cache_count"), _plugin.BuiltinCacheCount);
                SetText(_engineState, L("engine_builtin") + "\n" + cache + voiceLines,
                    _plugin.BuiltinCacheCount > 0 && !voiceWarn ? Dim : Off);
                // 캐시 폴더 경로는 ⓘ 툴팁으로 — "무전이 안 들린다" 진단의 첫 확인 지점이라
                // 숨기지 않고 블록에 붙여 둔다 (긴 경로가 매초 화면을 차지하지 않게)
                UpdateTip(_engineInfo, _engineMark, ref _engineTip,
                    cacheDir == null ? null : string.Format(L("cache_path_hint"), cacheDir),
                    _engineState);
                // 엔진이 시작할 때 읽은 뒤로 바뀐 설정 → [엔진 재시작] 옆 안내 (음성 출력은 바로 반영돼 제외)
                SetVisible(_enginePending, _plugin.EngineSettingsPending);

                RefreshUpdate();

                RecentCall[] calls = _plugin.RecentCallItems();
                var lines = new string[calls.Length];
                for (int i = 0; i < calls.Length; i++)
                    lines[i] = calls[i].Line + (calls[i].Dropped ? " " + L("recent_dropped") : "");
                SetText(_recent, calls.Length == 0
                    ? L("no_recent")
                    : L("recent_title") + "\n  " + string.Join("\n  ", lines), Dim);
                SetText(_testSpeak, _plugin.TestSpeakResult ?? "", Dim);
            }
            catch (Exception)
            {
                // 설정 화면 때문에 SimHub이 흔들리지 않게
            }
        }

        /// <summary>
        /// 텔레메트리 원천 한두 줄 — LMU 내장 메모리(DLL 불필요) / rF2 DLL / 대기 중 안내.
        /// 어떤 줄을 고를지는 Core(TelemetrySource.StatusLineKeys)가 정한다 (셀프테스트로 검사):
        /// 매핑이 있으면 Enable Plugins 안내(src_waiting)는 내지 않고, 형식 문제로 거부했으면 경고.
        /// </summary>
        private string SourceLine(out bool warn)
        {
            string[] keys = _plugin.TelemetryStatusKeys(out warn);
            var lines = new string[keys.Length];
            for (int i = 0; i < keys.Length; i++)
                lines[i] = L(keys[i]);
            return string.Join("\n", lines);
        }

        /// <summary>
        /// 음성 팩 줄들 ("\n"으로 시작, 없으면 ""). 팩이 없거나 설치가 끊겼거나 팩에 없는
        /// 멘트가 있으면 warn — 상태 블록을 경고색으로.
        /// </summary>
        private string VoiceLines(out bool warn)
        {
            warn = false;
            TeamRadio56Plugin.VoiceInfo info = _plugin.CurrentVoiceInfo();
            if (info == null)
                return "";
            var sb = new System.Text.StringBuilder();
            if (info.Note != null)
            {
                // 팩 없음/설치 미완료 → 무음 세션 ([음성 팩 업데이트] 안내), 일부만 → 경고
                warn = true;
                sb.Append("\n").Append(L("voice_note_" + info.Note.Replace('-', '_')));
            }
            else if (info.InstalledPack != null)
            {
                // 표식 없는 개발 캐시(확인 문장만 있음)는 팩 이름 줄을 생략
                sb.Append("\n").Append(string.Format(L("voice_pack_installed"),
                    PackName(info.InstalledPack)));
            }
            if (info.Dropped > 0)
                sb.Append("\n").Append(string.Format(L("voice_dropped"), info.Dropped));
            if (info.Missing > 0)
            {
                warn = true;
                sb.Append("\n").Append(string.Format(L("voice_pack_missing"), info.Missing));
            }
            return sb.ToString();
        }

        /// <summary>음성 팩이 없어(또는 설치가 끊겨) 이번 세션이 무음인가 — 상단 배너용.</summary>
        private bool PackMissing()
        {
            TeamRadio56Plugin.VoiceInfo info = _plugin.CurrentVoiceInfo();
            return info != null && !info.PackUsable;
        }

        /// <summary>업데이트 작업 스레드의 알림 — 여러 번 와도 UI 갱신은 한 번만 예약.</summary>
        private void OnUpdaterChanged()
        {
            if (System.Threading.Interlocked.Exchange(ref _updRefreshQueued, 1) == 1)
                return;
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    System.Threading.Interlocked.Exchange(ref _updRefreshQueued, 0);
                    RefreshUpdate();
                }));
            }
            catch (Exception)
            {
                System.Threading.Interlocked.Exchange(ref _updRefreshQueued, 0);
            }
        }

        private void SubscribeUpdater(bool on)
        {
            UpdateController u = _plugin.Updater;
            if (u == null)
                return;
            u.Changed -= OnUpdaterChanged;      // Loaded가 여러 번 와도 중복 구독 없게
            if (on)
                u.Changed += OnUpdaterChanged;
        }

        private void RefreshUpdate()
        {
            try
            {
                RefreshUpdateCore();
            }
            catch (Exception ex)
            {
                FileLog.Error("업데이트 화면 갱신 실패", ex);
            }
        }

        private void RefreshUpdateCore()
        {
            if (_updStatus == null)
                return;
            UpdateController u = _plugin.Updater;
            UpdateState st = u != null ? u.State : null;
            if (st == null)
            {
                SetText(_updVersions, string.Format(L("upd_versions_unknown"), TeamRadio56Plugin.Version), Fg);
                SetText(_updAudio, "", Dim);
                SetText(_updStatus, L("upd_unavailable"), Off);
                _btnUpdCheck.IsEnabled = _btnUpdPlugin.IsEnabled = _btnUpdAudio.IsEnabled = false;
                _btnUpdCancel.Visibility = Visibility.Collapsed;
                bool noPack = PackMissing();
                SetText(_updBanner, noPack ? L("pack_missing_banner") : "", Off);
                _updBanner.Visibility = noPack ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            UpdateCheck check = st.Check;
            string cur = TeamRadio56Plugin.Version;
            string versions;
            if (check == null)
                versions = string.Format(L("upd_versions_unknown"), cur);
            else
            {
                versions = string.Format(check.PluginUpdateAvailable
                    ? L("upd_versions_new") : L("upd_versions_same"), cur, check.Latest.Version);
                if (!string.IsNullOrEmpty(check.Latest.Notes))
                    versions += "\n" + string.Format(L("upd_notes"), check.Latest.Notes);
            }
            string last = FormatLastCheck(S != null ? S.LastUpdateCheck : null);
            if (last != null)
                versions += "\n" + string.Format(L("upd_last_check"), last);
            SetText(_updVersions, versions,
                    check != null && check.PluginUpdateAvailable && !st.RestartPending ? Accent : Fg);

            string audio;
            if (check == null)
                audio = L("upd_audio_unknown");
            else if (!check.Latest.HasAudioPack)
                audio = string.Format(L("upd_audio_none_in_release"), PackName(check.InstalledAudioPack));
            else if (check.AudioUpdateAvailable)
            {
                long size = check.Latest.AudioTotalSize;
                audio = string.Format(L("upd_audio_new"), PackName(check.InstalledAudioPack),
                    check.Latest.AudioPack,
                    size > 0 ? string.Format(L("upd_audio_size"), FormatBytes(size)) : "");
            }
            else
                audio = string.Format(L("upd_audio_same"), check.Latest.AudioPack);
            SetText(_updAudio, audio, check != null && check.AudioUpdateAvailable ? Accent : Fg);

            // 상태 줄: 진행 중 > 실패 > 완료 안내
            string status = "";
            Brush brush = Dim;
            if (st.Busy)
            {
                status = ProgressText(st);
                brush = Fg;
            }
            else if (st.ErrorKind.HasValue && st.ErrorKind.Value != UpdateErrorKind.RollbackFailed)
            {
                // Core의 상세 문구는 한국어 진단용이라 로그에만 — 화면엔 Loc 문구만
                status = L("upd_failed_" + st.FailedActivity) + L("upd_err_" + st.ErrorKind.Value);
                if (st.ErrorKind.Value != UpdateErrorKind.Cancelled)
                    status += "\n" + L("upd_see_log");
                brush = st.ErrorKind.Value == UpdateErrorKind.Cancelled ? Dim : Off;
            }
            if (!st.Busy)
            {
                var notes = new System.Collections.Generic.List<string>();
                if (st.ManualRestorePending)
                {
                    notes.Add(ManualRestoreText(st, u.PluginDir));
                    brush = Off;
                }
                if (status.Length > 0)
                    notes.Add(status);
                if (st.RestartPending)
                    notes.Add(string.Format(L("upd_restart"), st.RestartPendingVersion));
                if (st.AudioInstalledPack != null)
                    notes.Add(string.Format(L("upd_audio_done"), st.AudioInstalledPack));
                if (status.Length == 0 && notes.Count > 0 && !st.ManualRestorePending)
                    brush = Ok;
                status = string.Join("\n", notes);
            }
            SetText(_updStatus, status, brush);
            _updStatus.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            _btnUpdCheck.IsEnabled = !st.Busy;
            _btnUpdPlugin.IsEnabled = st.CanInstallPlugin;
            _btnUpdAudio.IsEnabled = st.CanInstallAudio;
            _btnUpdCancel.Visibility = st.CanCancel ? Visibility.Visible : Visibility.Collapsed;

            // 상단 배너 — 업데이트 섹션이 화면 맨 아래라 놓치지 않게
            string banner = null;
            Brush bannerBrush = Accent;
            if (st.ManualRestorePending)
            {
                banner = L("upd_banner_rollback");
                bannerBrush = Off;
            }
            else if (st.RestartPending)
            {
                banner = string.Format(L("upd_banner_restart"), st.RestartPendingVersion);
                bannerBrush = Ok;
            }
            else if (st.Activity != UpdateActivity.InstallingAudio && PackMissing())
            {
                // 무음 세션 — 무엇을 눌러야 하는지 맨 위에서 바로 알린다
                banner = L("pack_missing_banner");
                bannerBrush = Off;
            }
            else if (check != null && check.PluginUpdateAvailable)
                banner = string.Format(L("upd_banner_new"), check.Latest.Version);
            else if (check != null && check.AudioUpdateAvailable)
                banner = L("upd_banner_audio");
            SetText(_updBanner, banner ?? "", bannerBrush);
            _updBanner.Visibility = banner != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string ProgressText(UpdateState st)
        {
            UpdateProgress p = st.Progress;
            if (p == null)
                return st.Activity == UpdateActivity.Checking ? L("upd_checking") : L("upd_preparing");
            switch (p.Stage)
            {
                case UpdateStage.Downloading:
                    if (p.Total > 0)
                        return string.Format(L("upd_downloading"), p.Item, p.ItemCount, p.Name,
                            FormatBytes(p.Done), FormatBytes(p.Total),
                            (int)Math.Min(100, p.Done * 100 / p.Total));
                    return string.Format(L("upd_downloading_nototal"), p.Item, p.ItemCount, p.Name,
                        FormatBytes(p.Done));
                case UpdateStage.Verifying:
                    return string.Format(L("upd_verifying"), p.Item, p.ItemCount, p.Name);
                case UpdateStage.Extracting:
                    if (p.Total > 0)
                        return string.Format(L("upd_extracting_files"), p.Item, p.ItemCount, p.Name,
                            p.Done, p.Total);
                    return string.Format(L("upd_extracting"), p.Item, p.ItemCount, p.Name);
                case UpdateStage.Installing:
                    return L("upd_installing");
                default:
                    return L("upd_finishing");
            }
        }

        private static string PackName(string pack)
        {
            if (string.IsNullOrEmpty(pack))
                return L("upd_pack_none");
            if (pack.StartsWith(AudioPack.IncompletePrefix, StringComparison.Ordinal))
                return L("upd_pack_incomplete");
            return pack;
        }

        private static string FormatBytes(long bytes)
        {
            double mb = bytes / (1024.0 * 1024.0);
            return mb >= 10 ? mb.ToString("0") + " MB" : mb.ToString("0.0") + " MB";
        }

        /// <summary>저장된 UTC 왕복 시각 → 로컬 "yyyy-MM-dd HH:mm". 없거나 깨졌으면 null.</summary>
        private static string FormatLastCheck(string stored)
        {
            if (string.IsNullOrEmpty(stored))
                return null;
            DateTime t;
            if (!DateTime.TryParse(stored, System.Globalization.CultureInfo.InvariantCulture,
                                   System.Globalization.DateTimeStyles.RoundtripKind, out t))
                return null;
            return t.ToLocalTime().ToString("yyyy-MM-dd HH:mm",
                                            System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string ManualRestoreText(UpdateState st, string pluginDir)
        {
            var lines = new System.Collections.Generic.List<string>();
            foreach (var r in st.ManualRestore)
                lines.Add("  " + System.IO.Path.GetFileName(r.Key) + " → " + System.IO.Path.GetFileName(r.Value));
            return string.Format(L("upd_rollback_failed"), pluginDir, PluginSwap.BackupRecordName,
                                 string.Join("\n", lines));
        }

        // -- 위젯 헬퍼 -------------------------------------------------------

        /// <summary>
        /// 섹션 제목 (+ 설명이 있으면 ⓘ와 툴팁). 섹션 설명 문단은 제목에 마우스를 올렸을 때만.
        /// </summary>
        private StackPanel Section(StackPanel parent, string title, string hint = null)
        {
            // 제목 폭만큼(Left)만, 투명 배경으로 — 글자와 ⓘ 사이 여백에서도 툴팁이 닫히지 않게
            var head = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = HitArea,
                Margin = new Thickness(0, 18, 0, 4),
            };
            var text = new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = Accent,
                VerticalAlignment = VerticalAlignment.Center,
            };
            head.Children.Add(text);
            if (!string.IsNullOrEmpty(hint))
                head.Children.Add(InfoMark());
            Tip(head, hint, text);
            parent.Children.Add(head);
            var panel = new StackPanel();
            parent.Children.Add(panel);
            return panel;
        }

        /// <summary>
        /// 라벨 | 컨트롤 한 줄. hint는 컨트롤 오른쪽 회색 글씨가 아니라 줄 전체(라벨 칸·ⓘ·컨트롤)의
        /// 툴팁이다. 줄은 투명 배경이라 라벨 칸의 빈 곳과 ⓘ·컨트롤 사이 틈도 줄의 툴팁 영역 —
        /// 라벨에서 컨트롤로 옮겨 가도 툴팁이 닫혔다 다시 뜨지 않는다.
        /// </summary>
        private StackPanel Row(StackPanel parent, string label, UIElement control, string hint = null)
        {
            bool hasHint = !string.IsNullOrEmpty(hint);
            // 폭은 내용만큼(Left) — 줄 오른쪽 빈 화면에서까지 툴팁이 뜨지 않게
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = HitArea,
                Margin = new Thickness(0, 3, 0, 3),
            };
            // 라벨 칸은 170px 고정 — ⓘ가 붙어도 컨트롤 열이 줄을 맞춘다 (ⓘ 몫 20px은 라벨 폭에서)
            var cell = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Width = 170,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var text = new TextBlock
            {
                Text = label,
                MaxWidth = hasHint ? 150 : 170,
                Foreground = Fg,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            cell.Children.Add(text);
            if (hasHint)
                cell.Children.Add(InfoMark());
            row.Children.Add(cell);
            row.Children.Add(control);
            // 스크린 리더는 포커스를 받은 컨트롤의 이름(라벨)과 HelpText(설명)를 읽는다 —
            // 슬라이더 줄은 컨트롤이 패널이라 그 안의 Slider에
            UIElement focus = FocusTarget(control);
            if (focus != null)
                AutomationProperties.SetLabeledBy(focus, text);
            Tip(row, hint, focus ?? text);
            parent.Children.Add(row);
            return row;   // 호출부가 Visibility를 토글할 수 있게
        }

        /// <summary>컨트롤이면 그 자신, 패널이면 그 안의 첫 컨트롤 (슬라이더 줄의 Slider). 없으면 null.</summary>
        private static UIElement FocusTarget(UIElement el)
        {
            if (el is Control)
                return el;
            var panel = el as Panel;
            if (panel == null)
                return null;
            foreach (UIElement child in panel.Children)
            {
                UIElement found = FocusTarget(child);
                if (found != null)
                    return found;
            }
            return null;
        }

        /// <summary>
        /// 호버 설명(툴팁)을 단다. 빈 글이면 툴팁을 지운다.
        /// 글자색·배경은 SimHub 테마의 ToolTip 스타일에 맡긴다 — 여기서 Fg(Gainsboro)를 박으면
        /// 밝은 툴팁 위에서 글씨가 안 보인다. 긴 설명은 TipMaxWidth에서 줄바꿈.
        /// 같은 글을 스크린 리더용 HelpText로 reader(없으면 el)에 — 패널은 AutomationPeer가 없어
        /// 거기 단 HelpText는 아무도 읽지 못하므로, 툴팁을 패널에 달 때는 컨트롤·TextBlock을 넘긴다.
        /// </summary>
        private static void Tip(FrameworkElement el, string text, UIElement reader = null)
        {
            if (el == null)
                return;
            UIElement help = reader ?? el;
            if (string.IsNullOrEmpty(text))
            {
                el.ClearValue(FrameworkElement.ToolTipProperty);
                help.ClearValue(AutomationProperties.HelpTextProperty);
                return;
            }
            el.ToolTip = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = TipMaxWidth,
            };
            ToolTipService.SetShowDuration(el, TipShowMs);
            ToolTipService.SetInitialShowDelay(el, TipDelayMs);
            ToolTipService.SetShowOnDisabled(el, true);
            AutomationProperties.SetHelpText(help, text);
        }

        /// <summary>
        /// 매초 갱신되는 블록의 툴팁 — 글이 바뀔 때만 Tip을 부른다 (매초 새 툴팁을 만들면
        /// 열려 있던 툴팁이 깜빡인다). 빈 글이면 툴팁과 ⓘ를 함께 숨긴다. reader는 Tip과 같다.
        /// </summary>
        private static void UpdateTip(FrameworkElement owner, FrameworkElement mark,
                                      ref string current, string text, UIElement reader)
        {
            if (string.IsNullOrEmpty(text))
                text = null;
            if (string.Equals(current, text, StringComparison.Ordinal))
                return;
            current = text;
            Tip(owner, text, reader);
            mark.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 설명이 있다는 표시 (ⓘ). 글꼴과 무관하게 그린다 — ⓘ 글리프가 없는 글꼴도 있고,
        /// "i" 글자는 줄 높이가 14px 원 안쪽보다 커서 잘릴 수 있다. 원은 Border, i는 점 + 막대.
        /// 툴팁은 따로 달지 않는다 — 부모(줄·제목·블록)의 툴팁을 물려받는다.
        /// </summary>
        private static Border InfoMark()
        {
            var glyph = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            glyph.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = 2,
                Height = 2,
                Fill = Dim,
                Margin = new Thickness(0, 0, 0, 1),
            });
            glyph.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = 2,
                Height = 5,
                Fill = Dim,
            });
            return new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                BorderBrush = Dim,
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent,   // 원 안쪽도 마우스가 걸리게
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                UseLayoutRounding = true,           // 2px 점·막대가 흐려지지 않게 픽셀에 맞춘다
                Cursor = System.Windows.Input.Cursors.Help,
                Child = glyph,
            };
        }

        private UIElement MakeCheck(bool value, Action<bool> setter)
        {
            var box = new CheckBox
            {
                IsChecked = value,
                Foreground = Fg,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 60,
            };
            box.Click += (s, e) =>
            {
                setter(box.IsChecked == true);
                Save();
            };
            return box;
        }

        private UIElement MakeSlider(double min, double max, double step, double value,
                                     Action<double> setter, string format)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = Math.Max(min, Math.Min(max, value)),
                Width = 170,
                TickFrequency = step,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var label = new TextBlock
            {
                Text = string.Format(format, slider.Value),
                Width = 90,
                Foreground = Fg,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            slider.ValueChanged += (s, e) =>
            {
                label.Text = string.Format(format, slider.Value);
                setter(slider.Value);
                Save();
            };
            panel.Children.Add(slider);
            panel.Children.Add(label);
            return panel;
        }

        private UIElement MakeCombo(string[] items, string selected, Action<string> setter,
                                    Func<string, string> display = null)
        {
            // display: 저장값(items)은 그대로 두고 화면에만 설명 붙은 이름을 보여준다
            var combo = new ComboBox
            {
                Width = display == null ? 240 : 320,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var values = new System.Collections.Generic.List<string>(items);
            int index = -1;
            for (int i = 0; i < values.Count; i++)
            {
                if (string.Equals(values[i], selected, StringComparison.OrdinalIgnoreCase))
                    index = i;
            }
            if (index < 0 && !string.IsNullOrEmpty(selected))
            {
                values.Add(selected);          // 목록에 없는 값도 유지
                index = values.Count - 1;
            }
            foreach (string v in values)
                combo.Items.Add(display != null ? display(v) : v);
            combo.SelectedIndex = index;
            combo.SelectionChanged += (s, e) =>
            {
                int i = combo.SelectedIndex;
                if (i >= 0 && i < values.Count)
                {
                    setter(values[i]);
                    Save();
                }
            };
            return combo;
        }

        private UIElement MakeText(string value, Action<string> setter)
        {
            var box = new TextBox
            {
                Text = value ?? "",
                Width = 240,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // 타이핑마다 저장하지 않고 포커스가 빠질 때만
            box.LostFocus += (s, e) =>
            {
                setter(box.Text);
                Save();
            };
            return box;
        }

        private UIElement MakeButton(string text, Action onClick)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 110,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(8, 3, 8, 3),
            };
            button.Click += (s, e) =>
            {
                try
                {
                    onClick();
                }
                catch (Exception ex)
                {
                    FileLog.Error("설정 화면 버튼 처리 실패", ex);
                }
            };
            return button;
        }

        private static void OpenFile(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(path);
            }
            catch (Exception ex)
            {
                FileLog.Error("파일 열기 실패: " + path, ex);
            }
        }
    }
}
