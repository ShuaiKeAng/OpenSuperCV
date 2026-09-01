using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SuperCV
{
    public class BookmarksData : ClipboardDataBase
    {

        public string Title { get; set; }

        public BookmarksData(
            Dictionary<TextFormat, string>? allTextList = null,
            string? launchTime = null,
            string? title = null)
            : base(allTextList, launchTime)
        {
            Title = title ?? string.Empty;
        }


        public void OnCVChanged(string newText)
        {
            AllTextList = new Dictionary<TextFormat, string>
            {
                [TextFormat.UnicodeText] = newText ?? string.Empty,
            };
        }

        
        

        // 重写基类的 OnPaste 方法，添加额外的延迟逻辑
        public override async void OnPaste(object? sender, EventArgs e)
        {
            try
            {
                base.OnPaste(sender, e);
                await Task.Delay(100);
                await MyClipboard.PasteWithMonitoringSuspendedAsync();
            }
            catch (Exception exception)
            {
                new AlertDialog($"粘贴失败：{exception.Message}").ShowDialog();
            }
        }



    }
}
