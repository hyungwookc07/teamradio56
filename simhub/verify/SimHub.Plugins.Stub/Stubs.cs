// SimHub 검증용 스텁 — 실제 SimHub 어셈블리가 없는 환경에서
// 플러그인 코드를 컴파일 검증하기 위한 최소 정의. 배포물에 포함 금지.
//
// 실제 SimHub API와 시그니처가 다르면 여기선 통과하고 사용자 PC에서 실패한다.
// 즉 이 스텁은 "문법/타입 오류"를 잡아줄 뿐, API 일치를 보장하지는 않는다.
using System;
using System.Windows.Controls;
using System.Windows.Media;
using GameReaderCommon;

namespace SimHub.Plugins
{
    public class PluginManager
    {
        public string GameName { get; set; }
        public object GetPropertyValue(string name) { return null; }
    }

    public interface IPlugin
    {
        PluginManager PluginManager { get; set; }
        void Init(PluginManager pluginManager);
        void End(PluginManager pluginManager);
    }

    // 실제 SimHub의 프로퍼티 노출 확장 (Dash Studio 등에서 사용)
    public static class PluginExtensions
    {
        public static void AttachDelegate<T>(this IPlugin plugin, string name,
                                             Func<T> valueProvider) { }
    }

    public interface IDataPlugin
    {
        void DataUpdate(PluginManager pluginManager, ref GameData data);
    }

    public interface IWPFSettingsV2
    {
        string LeftMenuTitle { get; }
        ImageSource PictureIcon { get; }
        Control GetWPFSettingsControl(PluginManager pluginManager);
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class PluginNameAttribute : Attribute
    {
        public PluginNameAttribute(string name) { Name = name; }
        public string Name { get; private set; }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class PluginAuthorAttribute : Attribute
    {
        public PluginAuthorAttribute(string author) { Author = author; }
        public string Author { get; private set; }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class PluginDescriptionAttribute : Attribute
    {
        public PluginDescriptionAttribute(string description) { Description = description; }
        public string Description { get; private set; }
    }
}
