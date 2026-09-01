using System;
using System.Collections.Generic;

namespace SuperCV
{
    public abstract class ClipboardDataBase
    {
        private Dictionary<TextFormat, string> _allTextList = new();


        public virtual Dictionary<TextFormat, string> AllTextList
        {
            get => _allTextList;
            set => _allTextList = value is null
                ? new Dictionary<TextFormat, string>()
                : new Dictionary<TextFormat, string>(value);
        }

        public string LaunchTime { get; set; } = string.Empty;
        public int Tag { get; set; } = 0;

        public bool TopMost
        {
            get; set;
        }
        protected ClipboardDataBase(
            Dictionary<TextFormat, string>? allTextList = null,
            string? launchTime = null,
            int tag = 0,
            bool topMost = false)
        {
            AllTextList = allTextList ?? new Dictionary<TextFormat, string>();
            LaunchTime = launchTime ?? string.Empty;
            Tag = tag;
            TopMost = topMost;
        }

        // 基础的粘贴逻辑
        public virtual void OnPaste(object? sender, EventArgs e)
        {
            // 基础实现：将数据设置到剪贴板
            MyClipboard.SetMultipleTextFormats(AllTextList);
        }


    }

}
