# 0.4.1 工具窗口外观

工具栏改为 26px 图标按钮，使用 VS KnownMonikers 与 CrispImage。文件/目标通过下拉框切换；齿轮展开工作区、目录与查询设置。新建及批量引用管理移至加号菜单和树的右键菜单，没有删除原有操作。

按钮、文本框、下拉框、菜单及列表接入 VsResourceKeys 样式。文件树使用 20px 最小行高、文件类型图标及整行选中背景，选中与失焦色来自 [VS TreeViewColors](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.platformui.treeviewcolors?view=visualstudiosdk-2022)。动态主题资源负责响应主题变化，无固定深色颜色值。弹窗使用 DialogWindow。

状态信息保留底部摘要，长内容在工具提示中完整显示。工具栏按钮有文字提示和自动化名称；需要目标的菜单项在未选择目标时禁用。

验证：VS 2022 实验实例深色主题下实际打开 ui-03 工程，确认紧凑工具栏、目录图标、整行选择、管理菜单与弹窗内容配色。VS 2022 / VS 2026 均构建成功并部署实验实例。DialogWindow 标题栏最终调整通过双版本构建，尚未重新做目视验证；浅色、蓝色和高对比主题尚未逐一目视验收。

0.4.1 VSIX 需要重新安装并重启日常 VS 才生效；开发脚本只部署到 CMakePlus 实验实例。
