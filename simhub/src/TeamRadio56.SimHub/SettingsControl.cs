using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TeamRadio56.Core.Config;
using TeamRadio56.Core.Diagnostics;
using TeamRadio56.Core.Update;

namespace TeamRadio56.SimHub
{
    /// <summary>
    /// SimHub 좌측 메뉴에 붙는 설정 화면.
    ///
    /// XAML 없이 코드로 조립한다 — x:Class 매칭이나 .g.cs 생성 같은
    /// XAML 빌드 실패 지점을 없애기 위함 (개발 환경에서 컴파일 검증 불가).
    /// 문자열은 전부 Loc(ko/en)을 거친다 — 화면 언어를 바꾸면 즉시 재조립.
    /// </summary>
    public class SettingsControl : UserControl
    {
        private static readonly Brush Fg = Brushes.Gainsboro;
        private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x90, 0x96, 0x9C));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
        private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84));
        private static readonly Brush Off = new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73));

        private readonly TeamRadio56Plugin _plugin;
        // 화면을 다시 그릴 때마다 새로 만든다 — WPF는 이미 다른 패널에 붙은
        // 요소를 새 패널에 붙이면 예외를 던져, 재조립(언어/모드 전환)이 실패한다
        private TextBlock _connection = new TextBlock();
        private TextBlock _status = new TextBlock();
        private TextBlock _recent = new TextBlock();
        private TextBlock _engineState = new TextBlock();
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
            header.Children.Add(new TextBlock
            {
                Text = "teamradio56",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = Accent,
            });
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
                Text = L("subtitle") + TeamRadio56Plugin.Version,
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
            // LLM 섹션은 C# 포팅 전까지 보류 — 기능이 준비되면 BuildLlm(root) 복원
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
        }

        private void BuildEngine(StackPanel root)
        {
            StackPanel box = Section(root, L("sec_engine"));
            box.Children.Add(Hint(L("engine_hint")));

            _engineState = new TextBlock();
            _engineState.Foreground = Dim;
            _engineState.TextWrapping = TextWrapping.Wrap;
            _engineState.Margin = new Thickness(0, 2, 0, 6);
            box.Children.Add(_engineState);

            bool python = !string.Equals(S.EngineMode, "builtin",
                                         StringComparison.OrdinalIgnoreCase);

            Row(box, L("row_mode"), MakeCombo(PluginSettings.EngineChoices, S.EngineMode, v =>
            {
                if (string.Equals(S.EngineMode, v, StringComparison.OrdinalIgnoreCase))
                    return;
                S.EngineMode = v;
                // 재시작은 프로세스 종료 대기 등으로 수 초 걸릴 수 있다 —
                // UI 스레드에서 돌리면 화면 전체가 얼어붙으므로 백그라운드로
                RebuildLater();            // 모드에 따라 보이는 항목이 달라진다
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        _plugin.RestartEngine();   // 즉시 전환 (이전 모드 정리 → 새 모드 기동)
                    }
                    catch (Exception ex)
                    {
                        FileLog.Error("모드 전환 실패", ex);
                    }
                    Dispatcher.BeginInvoke(new Action(Refresh));
                });
            }, Choice("choice_")), L("hint_mode"));

            // builtin 모드: 사전 생성 오디오 캐시 폴더 (비우면 자동 탐색)
            if (!python)
            {
                Row(box, L("row_cache_dir"), MakeText(S.AudioCacheDir, v =>
                {
                    S.AudioCacheDir = v;
                }), L("hint_cache_dir"));
            }

            // 실행 파일/추가 인자/시작·중지는 파이썬 엔진 모드에만 의미가 있다
            if (python)
            {
                Row(box, L("row_exe"), MakeText(S.EngineExe, v =>
                {
                    S.EngineExe = v;
                }), L("hint_exe"));

                Row(box, L("row_args"), MakeText(S.EngineArgs, v =>
                {
                    S.EngineArgs = v;
                }), L("hint_args"));
            }

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 0),
            };
            if (python)
            {
                buttons.Children.Add(MakeButton(L("btn_engine_start"),
                    () => InBackground(() => _plugin.StartEngine())));
                buttons.Children.Add(MakeButton(L("btn_engine_stop"),
                    () => InBackground(_plugin.StopEngine)));
            }
            buttons.Children.Add(MakeButton(L("btn_engine_restart"),
                () => InBackground(_plugin.RestartEngine)));
            if (python)
                buttons.Children.Add(MakeButton(L("btn_engine_log"), () => OpenFile(_plugin.EngineLogPath)));
            box.Children.Add(buttons);

            box.Children.Add(Hint(L("engine_apply_hint")));
        }

        private void BuildVoice(StackPanel root)
        {
            StackPanel box = Section(root, L("sec_voice"));

            // 직관성 원칙: 지금 조합에서 의미 없는 항목은 아예 보여주지 않는다
            bool builtin = string.Equals(S.EngineMode, "builtin",
                                         StringComparison.OrdinalIgnoreCase);

            // 조건부 줄들은 항상 만들어 두고 Visibility만 바꾼다 —
            // 콤보 이벤트에서 화면 전체를 재조립하면 지연/씹힘이 생긴다.
            // 보이스/말 속도는 edge 엔진이거나 멘트 언어가 ko일 때 의미가 있다
            // (kokoro는 영어 전용이라 ko 멘트는 자동으로 edge 합성).
            StackPanel voiceRow = null, rateRow = null;
            Action syncVoiceRows = () =>
            {
                bool show = string.Equals(S.VoiceEngine, "edge",
                                StringComparison.OrdinalIgnoreCase)
                            || string.Equals(S.VoiceLanguage, "ko",
                                StringComparison.OrdinalIgnoreCase);
                Visibility v = show ? Visibility.Visible : Visibility.Collapsed;
                if (voiceRow != null)
                    voiceRow.Visibility = v;
                if (rateRow != null)
                    rateRow.Visibility = v;
            };

            Row(box, L("row_voice_lang"),
                MakeCombo(PluginSettings.VoiceLanguageChoices, S.VoiceLanguage, v =>
                {
                    S.VoiceLanguage = v;
                    syncVoiceRows();
                }, Choice("choice_lang_")), L("hint_voice_lang"));

            Row(box, L("row_voice_on"), MakeCheck(S.VoiceEnabled, v =>
            {
                S.VoiceEnabled = v;
            }), L("hint_voice_on"));

            Row(box, L("row_voice_engine"),
                MakeCombo(PluginSettings.VoiceEngineChoices, S.VoiceEngine, v =>
                {
                    S.VoiceEngine = v;
                    syncVoiceRows();
                }, Choice("choice_")), L("hint_voice_engine"));

            voiceRow = Row(box, L("row_voice"),
                MakeCombo(PluginSettings.VoiceChoices, S.EdgeVoice, v =>
                {
                    S.EdgeVoice = v;
                }, Choice("voice_")), L("hint_voice"));

            rateRow = Row(box, L("row_rate"), MakeSlider(-20, 40, 5, S.SpeechRatePercent, v =>
            {
                S.SpeechRatePercent = (int)Math.Round(v);
            }, "{0:+0;-0;0}%"));
            syncVoiceRows();

            // builtin 재생(SoundPlayer)은 볼륨/노이즈 조절이 없다 —
            // 노이즈는 캐시에 이미 구워져 있고, 볼륨은 윈도우 믹서로
            if (!builtin)
            {
                Row(box, L("row_volume"), MakeSlider(0.1, 1.0, 0.05, S.Volume, v =>
                {
                    S.Volume = v;
                }, "{0:P0}"));
            }

            Row(box, L("row_radiofx"), MakeCheck(S.RadioFx, v =>
            {
                S.RadioFx = v;
            }), L("hint_radiofx"));

            if (!builtin)
            {
                Row(box, L("row_noise"), MakeSlider(0.0, 0.02, 0.002, S.RadioNoise, v =>
                {
                    S.RadioNoise = v;
                }, "{0:0.000}"), L("hint_noise"));
            }
        }

        private void BuildChatter(StackPanel root)
        {
            StackPanel box = Section(root, L("sec_chatter"));
            Row(box, L("row_preset"), MakeCombo(PluginSettings.ChatterChoices, S.ChatterPreset, v =>
            {
                S.ChatterPreset = v;
            }, Choice("choice_")), L("hint_preset"));
            box.Children.Add(Hint(L("chatter_hint")));
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
            StackPanel box = Section(root, L("sec_reports"));
            box.Children.Add(Hint(L("reports_hint")));

            Row(box, L("row_laptime"), MakeCheck(S.LapTimeEveryLap, v =>
            {
                S.LapTimeEveryLap = v;
            }), L("hint_laptime"));

            Row(box, L("row_status_report"), MakeSlider(0, 10, 1, S.StatusEveryLaps, v =>
            {
                S.StatusEveryLaps = (int)Math.Round(v);
            }, L("fmt_every_laps")), L("hint_status_report"));
        }

        private void BuildLlm(StackPanel root)
        {
            StackPanel box = Section(root, L("sec_llm"));

            // builtin 모드는 LLM 미지원 — 죽은 설정을 늘어놓는 대신 한 줄로 안내
            if (string.Equals(S.EngineMode, "builtin", StringComparison.OrdinalIgnoreCase))
            {
                box.Children.Add(Hint(L("llm_builtin_note")));
                return;
            }
            box.Children.Add(Hint(L("llm_hint")));

            Row(box, L("row_llm_on"), MakeCheck(S.LlmEnabled, v =>
            {
                S.LlmEnabled = v;
            }));

            Row(box, L("row_api_key"), MakeText(S.LlmApiKey, v =>
            {
                S.LlmApiKey = v;
            }), L("hint_api_key"));

            Row(box, L("row_budget"), MakeSlider(0, 60, 5, S.LlmBudgetPerHour, v =>
            {
                S.LlmBudgetPerHour = (int)Math.Round(v);
            }, L("fmt_calls")), L("hint_budget"));
        }

        private void BuildBehaviour(StackPanel root)
        {
            StackPanel box = Section(root, L("sec_behaviour"));

            Row(box, L("row_realtime"), MakeCheck(S.RequireRealtime, v =>
            {
                S.RequireRealtime = v;
            }), L("hint_realtime"));

            // 발화 기록(JSONL)은 파이썬 엔진 전용 — builtin은 플러그인 로그에 항상 남는다
            if (!string.Equals(S.EngineMode, "builtin", StringComparison.OrdinalIgnoreCase))
            {
                Row(box, L("row_speech_log"), MakeCheck(S.SpeechLog, v =>
                {
                    S.SpeechLog = v;
                }), L("hint_speech_log"));
            }
        }


        private void BuildUpdate(StackPanel root)
        {
            StackPanel box = Section(root, L("sec_update"));
            box.Children.Add(Hint(L("update_hint")));

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
        /// 엔진 시작/중지/재시작은 프로세스 종료 대기·WMI 검색·보이스 워커
        /// 정지(최대 10초)를 포함한다 — UI 스레드에서 돌리면 SimHub 화면이 언다.
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

        private void Refresh()
        {
            try
            {
                bool connected = _plugin.IsConnected;
                SetText(_connection, connected ? L("conn_on") : L("conn_off"),
                        connected ? Ok : Off);
                SetText(_status, _plugin.StatusText ?? "", Fg);

                if (_plugin.UsingPythonEngine)
                {
                    string error = _plugin.EngineError;
                    if (!string.IsNullOrEmpty(error))
                        SetText(_engineState, L("engine_error") + error, Off);
                    else if (_plugin.EngineRunning)
                        SetText(_engineState,
                                L("engine_running") + _plugin.EngineExePath(), Ok);
                    else
                        SetText(_engineState,
                                L("engine_stopped") + _plugin.EngineExePath(), Dim);
                }
                else
                {
                    // 캐시 상태를 함께 — "코코로가 안 나온다" 진단의 첫 확인 지점
                    string cache = _plugin.BuiltinCacheDir == null
                        ? L("cache_missing")
                        : string.Format(L("cache_status"),
                            _plugin.BuiltinCacheCount, _plugin.BuiltinCacheDir);
                    SetText(_engineState, L("engine_builtin") + "\n" + cache,
                        _plugin.BuiltinCacheCount > 0 ? Dim : Off);
                }

                RefreshUpdate();

                string[] calls = _plugin.RecentCalls();
                SetText(_recent, calls.Length == 0
                    ? L("no_recent")
                    : L("recent_title") + "\n  " + string.Join("\n  ", calls), Dim);
            }
            catch (Exception)
            {
                // 설정 화면 때문에 SimHub이 흔들리지 않게
            }
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
                _updBanner.Visibility = Visibility.Collapsed;
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

        private StackPanel Section(StackPanel parent, string title)
        {
            parent.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = Accent,
                Margin = new Thickness(0, 18, 0, 4),
            });
            var panel = new StackPanel();
            parent.Children.Add(panel);
            return panel;
        }

        private TextBlock Hint(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = Dim,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            };
        }

        private StackPanel Row(StackPanel parent, string label, UIElement control, string hint = null)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 3, 0, 3),
            };
            row.Children.Add(new TextBlock
            {
                Text = label,
                Width = 170,
                Foreground = Fg,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            });
            row.Children.Add(control);
            if (!string.IsNullOrEmpty(hint))
            {
                row.Children.Add(new TextBlock
                {
                    Text = hint,
                    Foreground = Dim,
                    Margin = new Thickness(12, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            parent.Children.Add(row);
            return row;   // 호출부가 Visibility를 토글할 수 있게
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
