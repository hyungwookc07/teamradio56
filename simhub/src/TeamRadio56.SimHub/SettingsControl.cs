using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TeamRadio56.Core.Config;
using TeamRadio56.Core.Diagnostics;

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
        private readonly TextBlock _connection = new TextBlock();
        private readonly TextBlock _status = new TextBlock();
        private readonly TextBlock _recent = new TextBlock();
        private readonly TextBlock _engineState = new TextBlock();
        private readonly DispatcherTimer _timer = new DispatcherTimer();

        public SettingsControl(TeamRadio56Plugin plugin)
        {
            _plugin = plugin;
            Loc.Lang = (S != null ? S.UiLanguage : null) ?? "ko";
            Build();

            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (s, e) => Refresh();
            Loaded += (s, e) => { Refresh(); _timer.Start(); };
            Unloaded += (s, e) => _timer.Stop();
        }

        private PluginSettings S { get { return _plugin.Settings; } }

        private static string L(string key)
        {
            return Loc.L(key);
        }

        private void Save()
        {
            _plugin.SaveSettings();
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
                    Build();       // 새 언어로 재조립
                });
            ((FrameworkElement)langCombo).Width = 80;
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

            BuildStatus(root);
            BuildEngine(root);
            BuildVoice(root);
            BuildChatter(root);
            BuildTraffic(root);
            BuildReports(root);
            BuildLlm(root);
            BuildBehaviour(root);

            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = root,
            };
        }

        private void BuildStatus(StackPanel root)
        {
            StackPanel box = Section(root, L("sec_status"));

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
                _plugin.RestartEngine();   // 즉시 전환 (이전 모드 정리 → 새 모드 기동)
                Build();                   // 모드에 따라 보이는 항목이 달라진다
            }), L("hint_mode"));

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
                buttons.Children.Add(MakeButton(L("btn_engine_start"), () => _plugin.StartEngine()));
                buttons.Children.Add(MakeButton(L("btn_engine_stop"), () => _plugin.StopEngine()));
            }
            buttons.Children.Add(MakeButton(L("btn_engine_restart"), () => _plugin.RestartEngine()));
            if (python)
                buttons.Children.Add(MakeButton(L("btn_engine_log"), () => OpenFile(_plugin.EngineLogPath)));
            box.Children.Add(buttons);

            box.Children.Add(Hint(L("engine_apply_hint")));
        }

        private void BuildVoice(StackPanel root)
        {
            StackPanel box = Section(root, L("sec_voice"));

            Row(box, L("row_voice_lang"),
                MakeCombo(PluginSettings.VoiceLanguageChoices, S.VoiceLanguage, v =>
                {
                    S.VoiceLanguage = v;
                }), L("hint_voice_lang"));

            Row(box, L("row_voice_on"), MakeCheck(S.VoiceEnabled, v =>
            {
                S.VoiceEnabled = v;
            }), L("hint_voice_on"));

            Row(box, L("row_voice"), MakeCombo(PluginSettings.VoiceChoices, S.EdgeVoice, v =>
            {
                S.EdgeVoice = v;
            }), L("hint_voice"));

            Row(box, L("row_rate"), MakeSlider(-20, 40, 5, S.SpeechRatePercent, v =>
            {
                S.SpeechRatePercent = (int)Math.Round(v);
            }, "{0:+0;-0;0}%"));

            Row(box, L("row_volume"), MakeSlider(0.1, 1.0, 0.05, S.Volume, v =>
            {
                S.Volume = v;
            }, "{0:P0}"));

            Row(box, L("row_radiofx"), MakeCheck(S.RadioFx, v =>
            {
                S.RadioFx = v;
            }), L("hint_radiofx"));

            Row(box, L("row_noise"), MakeSlider(0.0, 0.02, 0.002, S.RadioNoise, v =>
            {
                S.RadioNoise = v;
            }, "{0:0.000}"), L("hint_noise"));
        }

        private void BuildChatter(StackPanel root)
        {
            StackPanel box = Section(root, L("sec_chatter"));
            Row(box, L("row_preset"), MakeCombo(PluginSettings.ChatterChoices, S.ChatterPreset, v =>
            {
                S.ChatterPreset = v;
            }), L("hint_preset"));
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

            Row(box, L("row_speech_log"), MakeCheck(S.SpeechLog, v =>
            {
                S.SpeechLog = v;
            }), L("hint_speech_log"));
        }

        // -- 상태 갱신 -------------------------------------------------------

        private void Refresh()
        {
            try
            {
                bool connected = _plugin.IsConnected;
                _connection.Text = connected ? L("conn_on") : L("conn_off");
                _connection.Foreground = connected ? Ok : Off;
                _status.Text = _plugin.StatusText ?? "";

                if (_plugin.UsingPythonEngine)
                {
                    string error = _plugin.EngineError;
                    if (!string.IsNullOrEmpty(error))
                    {
                        _engineState.Text = L("engine_error") + error;
                        _engineState.Foreground = Off;
                    }
                    else if (_plugin.EngineRunning)
                    {
                        _engineState.Text = L("engine_running") + _plugin.EngineExePath();
                        _engineState.Foreground = Ok;
                    }
                    else
                    {
                        _engineState.Text = L("engine_stopped") + _plugin.EngineExePath();
                        _engineState.Foreground = Dim;
                    }
                }
                else
                {
                    _engineState.Text = L("engine_builtin");
                    _engineState.Foreground = Dim;
                }

                string[] calls = _plugin.RecentCalls();
                _recent.Text = calls.Length == 0
                    ? L("no_recent")
                    : L("recent_title") + "\n  " + string.Join("\n  ", calls);
            }
            catch (Exception)
            {
                // 설정 화면 때문에 SimHub이 흔들리지 않게
            }
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

        private void Row(StackPanel parent, string label, UIElement control, string hint = null)
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

        private UIElement MakeCombo(string[] items, string selected, Action<string> setter)
        {
            var combo = new ComboBox
            {
                Width = 240,
                VerticalAlignment = VerticalAlignment.Center,
            };
            int index = -1;
            for (int i = 0; i < items.Length; i++)
            {
                combo.Items.Add(items[i]);
                if (string.Equals(items[i], selected, StringComparison.OrdinalIgnoreCase))
                    index = i;
            }
            if (index < 0 && !string.IsNullOrEmpty(selected))
            {
                combo.Items.Add(selected);     // 목록에 없는 값도 유지
                index = combo.Items.Count - 1;
            }
            combo.SelectedIndex = index;
            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedItem != null)
                {
                    setter(combo.SelectedItem.ToString());
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
