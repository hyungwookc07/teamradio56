using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TeamRadio56.SimHub
{
    /// <summary>
    /// SimHub 좌측 메뉴 아이콘.
    ///
    /// .resx 리소스나 SimHub의 ToIcon 확장에 의존하지 않도록 PNG를 base64로
    /// 코드에 직접 넣는다 (빌드 실패 지점 최소화).
    /// tools/gen_icon.py 로 생성 — 직접 수정하지 말 것.
    /// </summary>
    public static class PluginIcon
    {
        private static ImageSource _cached;

        private const string Base64 =
            "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAACi0lEQVR42u2XPUgjURSFg9VsJaQxjWlECwuxWEEJqC" +
            "BWItiIxG4RLWQt/AFREGxcf6pUgjZKQFMsdiIpXLASbbQQIVYKghAsNGgjCLNn4Dw4PCd/JqPLYuCD5M0k78w9597J" +
            "hFzXDX0moS8B/6qAKfAT/AADoANEP1JADtyDa3AGDsAGmKWghqAFPIEX9+3LE3ZCMWOgJSgB02AeLINNsA8uLFFXYI" +
            "s2RYMKoQPqQTuIg0WwB25EiGfRCuj6iC5wKGYCpMCdWLMDBkFNNQRMgnEwDHpAo8857bTpSKpxQEucanRBFmS4wTaY" +
            "A32gTs6rpTUpycchRdRU2gWvPh3giVmlEL3KXob1SSoxWGkXLIAE+A3ORZB3pWnapNZ0UoSpxE4pwSymMAxawRD4Bf" +
            "6IkAyr8d2qREoqtlKsRcvpAE9MP6uS4SZZflYRcQnmGfPwri4Y5YbN1rEmWnQsIlbFjlp2h2nRrUITM5+AR3ALTkGS" +
            "gtqsaoyLiAzPcaRFUzIxx8oV8Gx1wC2FxGWTMCth7EizO8xvTMjE3Mh3A8snYAYsgV1wKUIOaY0jdiQYzBdaUSdV2O" +
            "P3TngXLSuE3+jdCIU8i4i4nNfP7nAZvj4Z24sUluOt/N1d0A3WRURSMhFmi75yozmrIy7EhmglbdjNSphMTMqxIQ4r" +
            "l2O7UWzYl+nYUUxABMRIxEfEiGQiKS3ayolpbOjhej2no5kJA4UEeJuuMc1pvo9ZX2iRKpzSf2NDQlpyWHKwzPVrv6" +
            "GkV+5t+CCJf+BaxArmktgwKscWmIMsZ4RZn2cQ7/lH11dAjFdtv9I+VZhhGB+tHEzzbpgrsD5VDQGB/C0v1YJAnwtK" +
            "CWHgDybF2vD/ezb8C2zo1o+biHhHAAAAAElFTkSuQmCC";

        public static ImageSource Get()
        {
            if (_cached != null)
                return _cached;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = new MemoryStream(Convert.FromBase64String(Base64));
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                _cached = image;
            }
            catch (Exception)
            {
                _cached = null;   // 아이콘 때문에 플러그인이 죽지 않게
            }
            return _cached;
        }
    }
}
