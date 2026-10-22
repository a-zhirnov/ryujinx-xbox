using Ryujinx.HLE;
using Ryujinx.HLE.HOS.Applets;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.HOS.Services.Am.AppletOE.ApplicationProxyService.ApplicationProxy.Types;
using Ryujinx.HLE.UI;

namespace Ryujinx.Xbox.Core
{
    /// <summary>
    /// Host UI for the stage-0 core: no dialogs exist yet, so every applet request is declined.
    /// </summary>
    internal sealed class NullHostUiHandler : IHostUIHandler
    {
        public IHostUITheme HostUITheme { get; } = new NullHostUiTheme();

        public bool DisplayInputDialog(SoftwareKeyboardUIArgs args, out string userText)
        {
            userText = string.Empty;
            return false;
        }

        public bool DisplayMessageDialog(string title, string message) => false;

        public bool DisplayMessageDialog(ControllerAppletUIArgs args) => false;

        public bool DisplayCabinetDialog(out string userText)
        {
            userText = string.Empty;
            return false;
        }

        public void DisplayCabinetMessageDialog() { }

        public void ExecuteProgram(Switch device, ProgramSpecifyKind kind, ulong value) { }

        public bool DisplayErrorAppletDialog(string title, string message, string[] buttonsText, (uint Module, uint Description)? errorCode = null) => false;

        public IDynamicTextInputHandler CreateDynamicTextInputHandler() => new NullDynamicTextInputHandler();

        public UserProfile ShowPlayerSelectDialog() => null;

        public void TakeScreenshot() { }
    }

    internal sealed class NullHostUiTheme : IHostUITheme
    {
        public string FontFamily => "sans-serif";

        public ThemeColor DefaultBackgroundColor => new(1, 0, 0, 0);
        public ThemeColor DefaultForegroundColor => new(1, 1, 1, 1);
        public ThemeColor DefaultBorderColor => new(1, 1, 1, 1);
        public ThemeColor SelectionBackgroundColor => new(1, 1, 1, 1);
        public ThemeColor SelectionForegroundColor => new(1, 0, 0, 0);
    }

    internal sealed class NullDynamicTextInputHandler : IDynamicTextInputHandler
    {
#pragma warning disable CS0067 // events are never raised
        public event DynamicTextChangedHandler TextChangedEvent;
        public event KeyPressedHandler KeyPressedEvent;
        public event KeyReleasedHandler KeyReleasedEvent;
#pragma warning restore CS0067

        public bool TextProcessingEnabled { get; set; }

        public void SetText(string text, int cursorBegin) { }

        public void SetText(string text, int cursorBegin, int cursorEnd) { }

        public void Dispose() { }
    }
}
