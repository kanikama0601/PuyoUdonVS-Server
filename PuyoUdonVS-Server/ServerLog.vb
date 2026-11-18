''' <summary>
''' コンソールへのログ出力。複数のスレッドから呼ばれるのでロックして1行ずつ書く。
''' </summary>
Public Module ServerLog
    Private ReadOnly _lock As New Object()

    ''' <summary>trueなら PIECE / STATE / PING など頻繁なメッセージも表示する。</summary>
    Public Property Verbose As Boolean

    ''' <summary>"[時刻] 種別 内容" の形で1行表示する。種別は色付き。</summary>
    Public Sub Write(category As String, color As ConsoleColor, text As String)
        SyncLock _lock
            Dim old = Console.ForegroundColor
            Console.ForegroundColor = ConsoleColor.DarkGray
            Console.Write($"[{DateTime.Now:HH:mm:ss}] ")
            Console.ForegroundColor = color
            Console.Write(PadWide(category, 10))
            Console.ForegroundColor = old
            Console.WriteLine(" " & text)
        End SyncLock
    End Sub

    ''' <summary>Verbose のときだけ表示する。</summary>
    Public Sub Detail(category As String, text As String)
        If Verbose Then Write(category, ConsoleColor.DarkGray, text)
    End Sub

    ''' <summary>そのまま1行書く（起動時の案内など）。</summary>
    Public Sub Plain(text As String, Optional color As ConsoleColor = ConsoleColor.Gray)
        SyncLock _lock
            Dim old = Console.ForegroundColor
            Console.ForegroundColor = color
            Console.WriteLine(text)
            Console.ForegroundColor = old
        End SyncLock
    End Sub

    ''' <summary>全角文字を幅2として、表示幅が width になるよう空白で埋める。</summary>
    Private Function PadWide(s As String, width As Integer) As String
        Dim w = 0
        For Each ch In s
            w += If(AscW(ch) > &HFF, 2, 1)
        Next
        Return If(w >= width, s, s & New String(" "c, width - w))
    End Function
End Module
