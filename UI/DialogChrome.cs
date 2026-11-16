namespace WMonitor.UI;

/// <summary>
/// 无边框自绘对话框共用件（v0.36）。
/// 背景：更新弹窗（v0.34）用 CenterParent，但托盘菜单路径 <c>ShowDialog()</c> 无 owner ——
/// CenterParent 找不到父窗体直接落到屏幕原点，用户报告「锁定在左上角，无法移动」。
/// 三个对话框（UpdateDialog / MessageDialog / ActionConfirmDialog）同修：
/// 空白区 HTCAPTION 整窗可拖；UpdateDialog 定位改光标所在屏居中。
/// </summary>
public static class DialogChrome
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;

    /// <summary>
    /// 把父窗体空白区的命中改成标题栏 —— 整窗可拖。在各对话框 WndProc 的 base 之后调用。
    /// 按钮 / 勾选框等子控件有自己的命中测试，命中父窗体的只有空白区，点击不受影响。
    /// 双击拖拽区不会最大化：对话框一律 MaximizeBox=false（无边框无 WS_MAXIMIZEBOX，Snap 不生效）。
    /// </summary>
    public static void DragByClient(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST && (int)m.Result == HTCLIENT) m.Result = (IntPtr)HTCAPTION;
    }

    /// <summary>
    /// 定位到光标所在屏工作区的中心（垂直偏上 1/3）。给没有 owner 的 <c>ShowDialog()</c> 用 ——
    /// CenterParent 在那个路径下等于左上角。必须在 ClientSize 定完之后调（用 Width/Height 算位）。
    /// </summary>
    public static void CenterOnCursorScreen(Form f)
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(
            wa.Left + Math.Max(0, (wa.Width - f.Width) / 2),
            wa.Top + Math.Max(0, (wa.Height - f.Height) / 3));
    }
}
