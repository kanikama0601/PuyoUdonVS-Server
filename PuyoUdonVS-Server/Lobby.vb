Imports System.Text.Json

''' <summary>
''' すべてのルームを管理し、受信したメッセージを処理する（企画書 6.4）。
''' 状態はすべて1つのロックで守る。
''' </summary>
Public NotInheritable Class Lobby
    Private ReadOnly _lock As New Object()
    Private ReadOnly _rooms As New Dictionary(Of String, Room)
    Private _autoId As Integer

    ''' <summary>受信した1件のメッセージを処理する。False を返したら接続を閉じる（LEAVE）。</summary>
    Public Function Handle(c As ClientConnection, text As String) As Boolean
        Dim doc As JsonDocument
        Try
            doc = JsonDocument.Parse(text)
        Catch ex As JsonException
            Reject(c, ErrInvalidMessage, "JSON として読めません")
            Return True
        End Try

        Using doc
            Dim root = doc.RootElement
            Dim type = If(root.ValueKind = JsonValueKind.Object, GetString(root, "type"), Nothing)
            If type Is Nothing Then
                Reject(c, ErrInvalidMessage, """type"" がありません")
                Return True
            End If
            SyncLock _lock
                Select Case type
                    Case "PING" : OnPing(c, root)
                    Case "JOIN" : OnJoin(c, root)
                    Case "PIECE" : OnPiece(c, root)
                    Case "STATE" : OnState(c, root)
                    Case "CLEAR" : OnClear(c, root)
                    Case "GAME_OVER" : OnGameOver(c)
                    Case "REMATCH" : OnRematch(c)
                    Case "LEAVE"
                        OnLeave(c)
                        Return False
                    Case Else
                        Reject(c, ErrInvalidMessage, $"不明なメッセージ {type}")
                End Select
            End SyncLock
        End Using
        Return True
    End Function

    ''' <summary>接続が切れたときの後始末。</summary>
    Public Sub Disconnect(c As ClientConnection)
        SyncLock _lock
            If c.Room IsNot Nothing Then
                ServerLog.Write("退室", ConsoleColor.DarkYellow, $"{c.Label} が切断のため {c.Room.Label} から退室しました")
                Leave(c)
            End If
        End SyncLock
    End Sub

    ' =====================================================================
    ' 各メッセージの処理（ロック内で呼ばれる）
    ' =====================================================================

    Private Sub OnPing(c As ClientConnection, root As JsonElement)
        Dim t As Long
        If Not TryGetLong(root, "t", t) Then t = 0
        c.Send(Build("PONG", "t", t))
        ServerLog.Detail("PING", $"{c.Label} t={t}")
    End Sub

    Private Sub OnJoin(c As ClientConnection, root As JsonElement)
        If c.Room IsNot Nothing Then
            Reject(c, ErrInvalidState, $"すでに {c.Room.Label} にいるのに JOIN を受信")
            Return
        End If
        Dim name = GetString(root, "name")
        Dim roomId As String = ""
        Dim roomProp As JsonElement
        If root.TryGetProperty("room", roomProp) Then
            If roomProp.ValueKind <> JsonValueKind.String Then
                Reject(c, ErrInvalidMessage, "JOIN の room が文字列ではありません")
                Return
            End If
            roomId = roomProp.GetString().Trim()
        End If
        If name Is Nothing OrElse name.Trim().Length = 0 OrElse name.Length > MaxNameLength OrElse name.Any(AddressOf Char.IsControl) Then
            Reject(c, ErrInvalidName, $"ニックネームが正しくありません（「{name}」）")
            Return
        End If
        If roomId.Length > MaxRoomLength OrElse roomId.Any(AddressOf Char.IsControl) Then
            Reject(c, ErrInvalidMessage, "ルームIDが長すぎるか、使えない文字を含みます")
            Return
        End If
        name = name.Trim()

        Dim r As Room = Nothing
        If roomId = "" Then
            ' 自動マッチング: 1人で待っている自動ルームを探し、なければ作る
            r = _rooms.Values.FirstOrDefault(Function(x) x.IsAuto AndAlso x.Count = 1 AndAlso Not x.Playing)
            If r Is Nothing Then
                Dim id As String
                Do
                    _autoId += 1
                    id = $"auto-{_autoId}"
                Loop While _rooms.ContainsKey(id)
                r = NewRoom(id, isAuto:=True)
            End If
        ElseIf Not _rooms.TryGetValue(roomId, r) Then
            r = NewRoom(roomId, isAuto:=False)
        End If
        If r.Count >= 2 Then
            Reject(c, ErrRoomFull, $"{name} が満員の {r.Label} に入ろうとしました")
            Return
        End If

        Dim slot = If(r.Players(0) Is Nothing, 0, 1)
        r.Players(slot) = c
        c.Room = r
        c.Player = slot + 1
        c.Name = name
        c.WantsRematch = False
        c.Send(Build("JOINED", "player", c.Player, "room", r.Id))
        ServerLog.Write("入室", ConsoleColor.Cyan, $"{c.Label} が {r.Label} に入室しました（P{c.Player}）")

        If r.Count = 1 Then
            c.Send(Build("WAITING"))
            ServerLog.Write("待機", ConsoleColor.DarkCyan, $"{r.Label} {name} が相手を待っています")
        Else
            Dim p1 = r.Players(0), p2 = r.Players(1)
            ServerLog.Write("マッチ成立", ConsoleColor.Green, $"{r.Label} {p1.Name}（P1） vs {p2.Name}（P2）")
            Start(r)
        End If
    End Sub

    Private Sub OnPiece(c As ClientConnection, root As JsonElement)
        If Not RequirePlaying(c, "PIECE") Then Return
        Dim x, y, rot As Integer
        If Not (TryGetInt(root, "x", x) AndAlso TryGetInt(root, "y", y) AndAlso TryGetInt(root, "rot", rot)) OrElse
           x < 0 OrElse x >= Cols OrElse y < 0 OrElse y >= Rows OrElse rot < 0 OrElse rot > 3 Then
            Reject(c, ErrInvalidMessage, "PIECE の x / y / rot が正しくありません")
            Return
        End If
        c.Room.Other(c)?.Send(Build("OPP_PIECE", "x", x, "y", y, "rot", rot))
        ServerLog.Detail("PIECE", $"{c.Label} x={x} y={y} rot={rot}")
    End Sub

    Private Sub OnState(c As ClientConnection, root As JsonElement)
        If Not RequirePlaying(c, "STATE") Then Return
        Dim board = GetString(root, "board")
        Dim chain As Integer
        If Not IsValidBoard(board) OrElse Not TryGetInt(root, "chain", chain) OrElse chain < 0 Then
            Reject(c, ErrInvalidMessage, "STATE の board / chain が正しくありません")
            Return
        End If
        c.Room.Other(c)?.Send(Build("OPP_STATE", "board", board, "chain", chain))
        ServerLog.Detail("STATE", $"{c.Label} chain={chain} board={board}")
    End Sub

    Private Sub OnClear(c As ClientConnection, root As JsonElement)
        If Not RequirePlaying(c, "CLEAR") Then Return
        Dim erased, chain As Integer
        If Not (TryGetInt(root, "erased", erased) AndAlso TryGetInt(root, "chain", chain)) OrElse
           erased < 1 OrElse erased > Cols * Rows OrElse chain < 1 OrElse chain > Cols * Rows Then
            Reject(c, ErrInvalidMessage, "CLEAR の erased / chain が正しくありません")
            Return
        End If
        Dim count = GarbageCount(erased, chain)
        Dim opp = c.Room.Other(c)
        opp?.Send(Build("GARBAGE", "count", count))
        ServerLog.Write("消去", ConsoleColor.Yellow,
                        $"{c.Name}: {erased}個消去 {chain}連鎖 → おじゃまそば {count}個を {If(opp?.Name, "（相手なし）")} へ")
    End Sub

    Private Sub OnGameOver(c As ClientConnection)
        If Not RequirePlaying(c, "GAME_OVER") Then Return
        Dim r = c.Room
        Dim winner = If(c.Player = 1, 2, 1)
        ServerLog.Write("敗北", ConsoleColor.Magenta, $"{c.Name} がうどんを置けなくなりました")
        Finish(r, winner, "gameover")
    End Sub

    Private Sub OnRematch(c As ClientConnection)
        Dim r = c.Room
        Dim opp = r?.Other(c)
        If r Is Nothing OrElse r.Playing OrElse r.Games = 0 OrElse opp Is Nothing Then
            Reject(c, ErrInvalidState, "対戦の結果画面以外で REMATCH を受信")
            Return
        End If
        If c.WantsRematch Then Return
        c.WantsRematch = True
        opp.Send(Build("OPP_REMATCH"))
        ServerLog.Write("再戦希望", ConsoleColor.Cyan, $"{r.Label} {c.Name} が再戦を希望しています")
        If opp.WantsRematch Then
            ServerLog.Write("再戦", ConsoleColor.Green, $"{r.Label} {r.Players(0).Name}（P1） vs {r.Players(1).Name}（P2） 第{r.Games + 1}戦")
            Start(r)
        End If
    End Sub

    Private Sub OnLeave(c As ClientConnection)
        If c.Room IsNot Nothing Then
            ServerLog.Write("退室", ConsoleColor.DarkYellow, $"{c.Label} が {c.Room.Label} から退室しました")
            Leave(c)
        Else
            ServerLog.Detail("LEAVE", c.Label)
        End If
    End Sub

    ' =====================================================================
    ' ルームの操作
    ' =====================================================================

    Private Function NewRoom(id As String, isAuto As Boolean) As Room
        Dim r As New Room(id, isAuto)
        _rooms(id) = r
        Return r
    End Function

    Private Sub Start(r As Room)
        r.Seed = Random.Shared.Next(1, 1_000_000)
        r.Playing = True
        r.Games += 1
        For Each pl In r.Players
            pl.WantsRematch = False
            pl.Send(Build("START", "seed", r.Seed, "countdown", Countdown, "opponent", r.Other(pl).Name))
        Next
        ServerLog.Write("対戦開始", ConsoleColor.Green, $"{r.Label} seed={r.Seed} カウントダウン{Countdown}秒")
    End Sub

    Private Sub Finish(r As Room, winner As Integer, reason As String)
        r.Playing = False
        For Each pl In r.Players
            pl?.Send(Build("RESULT", "winner", winner, "reason", reason))
        Next
        Dim w = r.Players(winner - 1)
        Dim why = If(reason = "left", "相手の切断", "相手のゲームオーバー")
        ServerLog.Write("決着", ConsoleColor.Magenta, $"{r.Label} 勝者 {If(w?.Name, "?")}（P{winner}） 理由: {why}（{reason}）")
    End Sub

    ''' <summary>
    ''' c をルームから抜けさせる。相手がいれば OPP_LEFT（対戦中なら RESULT も）を送る。
    ''' 1人が抜けたら再戦はできないので、ルームは解散して残った人もルームから外す。
    ''' </summary>
    Private Sub Leave(c As ClientConnection)
        Dim r = c.Room
        If r Is Nothing Then Return
        Dim opp = r.Other(c)
        r.Players(c.Player - 1) = Nothing
        c.Room = Nothing
        If opp IsNot Nothing Then
            opp.Send(Build("OPP_LEFT"))
            If r.Playing Then Finish(r, opp.Player, "left")
            r.Players(opp.Player - 1) = Nothing
            opp.Room = Nothing
        End If
        r.Playing = False
        _rooms.Remove(r.Id)
        ServerLog.Write("ルーム解散", ConsoleColor.DarkGray, $"{r.Label} を閉じました（現在のルーム数 {_rooms.Count}）")
    End Sub

    Private Function RequirePlaying(c As ClientConnection, type As String) As Boolean
        If c.Room IsNot Nothing AndAlso c.Room.Playing Then Return True
        ' 決着直後の行き違いでよく起こるので、頻繁な PIECE / STATE は詳細表示のときだけ出す
        Dim why = $"対戦中でないのに {type} を受信"
        If type = "PIECE" OrElse type = "STATE" Then
            c.Send(ErrorMsg(ErrInvalidState))
            ServerLog.Detail("エラー", $"{c.Label} → {ErrInvalidState} {ErrorText(ErrInvalidState)}（{why}）")
        Else
            Reject(c, ErrInvalidState, why)
        End If
        Return False
    End Function

    ''' <summary>ERROR を返してメッセージを無視する。</summary>
    Private Shared Sub Reject(c As ClientConnection, code As Integer, why As String)
        c.Send(ErrorMsg(code))
        ServerLog.Write("エラー", ConsoleColor.Red, $"{c.Label} → {code} {ErrorText(code)}（{why}）")
    End Sub
End Class
