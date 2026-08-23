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
        private readonly DispatcherTimer _timer = new DispatcherTimer();

        public SettingsControl(TeamRadio56Plugin plugin)
        {
            _plugin = plugin;
            Build();

            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (s, e) => Refresh();
            Loaded += (s, e) => { Refresh(); _timer.Start(); };
            Unloaded += (s, e) => _timer.Stop();
        }

        private PluginSettings S { get { return _plugin.Settings; } }

        private void Save()
        {
            _plugin.SaveSettings();
        }

        // -- 화면 구성 -------------------------------------------------------

        private void Build()
        {
            var root = new StackPanel { Margin = new Thickness(14, 10, 14, 20) };

            root.Children.Add(new TextBlock
            {
                Text = "teamradio56",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = Accent,
            });
            root.Children.Add(new TextBlock
            {
                Text = "LMU AI 크루치프 — 상황을 판단해 영어 팀라디오로 불러줍니다. "
                       + "버전 " + TeamRadio56Plugin.Version,
                Foreground = Dim,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 4),
            });

            BuildStatus(root);
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
            StackPanel box = Section(root, "상태");

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
            buttons.Children.Add(MakeButton("테스트 발화", () => _plugin.TestSpeak()));
            buttons.Children.Add(MakeButton("로그 열기", () => OpenFile(FileLog.Path)));
            buttons.Children.Add(MakeButton("설정 파일 열기", () => OpenFile(SettingsStore.Path)));
            box.Children.Add(buttons);
        }

        private void BuildVoice(StackPanel root)
        {
            StackPanel box = Section(root, "음성");

            Row(box, "음성 출력", MakeCheck(S.VoiceEnabled, v =>
            {
                S.VoiceEnabled = v;
            }), "끄면 로그에만 남습니다");

            Row(box, "보이스", MakeCombo(PluginSettings.VoiceChoices, S.EdgeVoice, v =>
            {
                S.EdgeVoice = v;
            }), "바꾸면 오디오 캐시를 다시 만듭니다");

            Row(box, "말 속도", MakeSlider(-20, 40, 5, S.SpeechRatePercent, v =>
            {
                S.SpeechRatePercent = (int)Math.Round(v);
            }, "{0:+0;-0;0}%"));

            Row(box, "볼륨", MakeSlider(0.1, 1.0, 0.05, S.Volume, v =>
            {
                S.Volume = v;
            }, "{0:P0}"));

            Row(box, "무전기 효과", MakeCheck(S.RadioFx, v =>
            {
                S.RadioFx = v;
            }), "TTS 기계음을 팀라디오 질감으로");

            Row(box, "무전 노이즈", MakeSlider(0.0, 0.02, 0.002, S.RadioNoise, v =>
            {
                S.RadioNoise = v;
            }, "{0:0.000}"), "0이면 지직임 없음");
        }

        private void BuildChatter(StackPanel root)
        {
            StackPanel box = Section(root, "수다스러움");
            Row(box, "프리셋", MakeCombo(PluginSettings.ChatterChoices, S.ChatterPreset, v =>
            {
                S.ChatterPreset = v;
            }), "quiet = 꼭 필요한 콜만, chatty = 자주");
            box.Children.Add(Hint(
                "긴급 콜(나란히/충격/펑크/피트 리미터)은 프리셋과 무관하게 항상 나갑니다."));
        }

        private void BuildTraffic(StackPanel root)
        {
            StackPanel box = Section(root, "트래픽 / 스포터");

            Row(box, "나란히 판정 거리", MakeSlider(3.0, 10.0, 0.2, S.AlongsideMeters, v =>
            {
                S.AlongsideMeters = v;
            }, "{0:0.0} m"), "차 한 대 길이 ≈ 4.6m");

            Row(box, "스타트 스포터 모드", MakeSlider(0, 120, 5, S.StartSpotterSeconds, v =>
            {
                S.StartSpotterSeconds = v;
            }, "{0:0}초"), "혼전 구간엔 좌우 점유만 즉시 콜");

            Row(box, "좌우 반전", MakeCheck(S.SideInvert, v =>
            {
                S.SideInvert = v;
            }), "\"왼쪽/오른쪽\"이 반대로 들리면 켜세요");

            Row(box, "레이스에서만", MakeCheck(S.TrafficRaceOnly, v =>
            {
                S.TrafficRaceOnly = v;
            }), "연습/퀄리는 고스트 차가 많음");
        }

        private void BuildReports(StackPanel root)
        {
            StackPanel box = Section(root, "HUD 대체 정기 무전");
            box.Children.Add(Hint("HUD를 끄고 달릴 때 켜세요. 기본은 꺼짐(침묵 우선)."));

            Row(box, "매 랩 랩타임", MakeCheck(S.LapTimeEveryLap, v =>
            {
                S.LapTimeEveryLap = v;
            }), "\"Last lap 2 01.8. Best lap.\"");

            Row(box, "상황 리포트", MakeSlider(0, 10, 1, S.StatusEveryLaps, v =>
            {
                S.StatusEveryLaps = (int)Math.Round(v);
            }, "{0:0}랩마다"), "0 = 끔. 순위/갭/연료/타이어");
        }

        private void BuildLlm(StackPanel root)
        {
            StackPanel box = Section(root, "LLM 멘트");
            box.Children.Add(Hint(
                "여러 데이터를 엮은 판단형 멘트를 실시간 생성합니다. "
                + "꺼도 긴급 콜과 템플릿 멘트는 그대로 동작합니다."));

            Row(box, "사용", MakeCheck(S.LlmEnabled, v =>
            {
                S.LlmEnabled = v;
            }));

            Row(box, "API 키", MakeText(S.LlmApiKey, v =>
            {
                S.LlmApiKey = v;
            }), "비우면 환경변수 ANTHROPIC_API_KEY");

            Row(box, "시간당 호출 예산", MakeSlider(0, 60, 5, S.LlmBudgetPerHour, v =>
            {
                S.LlmBudgetPerHour = (int)Math.Round(v);
            }, "{0:0}회"), "2시간 레이스 기준 30회 이내 권장");
        }

        private void BuildBehaviour(StackPanel root)
        {
            StackPanel box = Section(root, "동작");

            Row(box, "주행 중에만 발화", MakeCheck(S.RequireRealtime, v =>
            {
                S.RequireRealtime = v;
            }), "모니터/메뉴에선 침묵");

            Row(box, "발화 로그", MakeCheck(S.SpeechLog, v =>
            {
                S.SpeechLog = v;
            }), "무슨 말을 언제 했는지 기록");
        }

        // -- 상태 갱신 -------------------------------------------------------

        private void Refresh()
        {
            try
            {
                bool connected = _plugin.IsConnected;
                _connection.Text = connected ? "● LMU 연결됨" : "○ 게임 대기 중";
                _connection.Foreground = connected ? Ok : Off;
                _status.Text = _plugin.StatusText ?? "";

                string[] calls = _plugin.RecentCalls();
                _recent.Text = calls.Length == 0
                    ? "최근 무전 없음"
                    : "최근 무전\n  " + string.Join("\n  ", calls);
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
                Width = 150,
                Foreground = Fg,
                VerticalAlignment = VerticalAlignment.Center,
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
                Width = 70,
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
