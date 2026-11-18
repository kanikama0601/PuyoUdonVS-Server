Imports System.Net.WebSockets
Imports System.Text
Imports System.Threading
Imports System.Threading.Channels

''' <summary>
''' 1つのクライアントとの WebSocket 接続。
''' 受信ループでメッセージを <see cref="Lobby"/> に渡し、送信はキューに積んで1本の送信タスクで順番に送る。
''' 以下のフィールド（Name / Room など）は Lobby のロック内でだけ読み書きする。
''' </summary>
Public NotInheritable Class ClientConnection
    Private Shared _nextId As Integer

    Private ReadOnly _ws As WebSocket
    Private ReadOnly _outbox As Channel(Of String) = Channel.CreateUnbounded(Of String)(New UnboundedChannelOptions With {.SingleReader = True})
    Private _lastReceiveTicks As Long = Environment.TickCount64
    Private _closing As Integer

    Public ReadOnly Id As Integer = Interlocked.Increment(_nextId)
    Public ReadOnly Address As String

    Public Name As String = ""
    Public Room As Room
    ''' <summary>ルーム内のプレイヤー番号（1 または 2）。</summary>
    Public Player As Integer
    Public WantsRematch As Boolean
    ''' <summary>タイムアウトで切断したとき true（切断ログの表示用）。</summary>
    Public TimedOut As Boolean

    Public Sub New(ws As WebSocket, address As String)
        _ws = ws
        Me.Address = address
    End Sub

    ''' <summary>ログに出す名前。JOIN 前は接続番号とアドレス。</summary>
    Public ReadOnly Property Label As String
        Get
            If Name <> "" Then Return $"{Name}({Address})"
            Return $"#{Id}({Address})"
        End Get
    End Property

    ''' <summary>1件のメッセージを送信キューに積む。</summary>
    Public Sub Send(json As String)
        If Volatile.Read(_closing) <> 0 Then Return
        _outbox.Writer.TryWrite(json)
    End Sub

    ''' <summary>切断されるまで受信を続ける。通信エラーで切れた場合はその説明を返す（正常終了なら Nothing）。</summary>
    Public Async Function RunAsync(lobby As Lobby) As Task(Of String)
        Dim failure As String = Nothing
        Using cts As New CancellationTokenSource()
            Dim sendTask = SendLoopAsync(cts.Token)
            Dim watchTask = WatchTimeoutAsync(cts.Token)
            Try
                Await ReceiveLoopAsync(lobby)
            Catch ex As WebSocketException
                failure = If(TimedOut, Nothing, "接続が途切れました")
            Catch ex As Exception
                failure = $"{ex.GetType().Name}: {ex.Message}"
            End Try

            MarkClosing()
            _outbox.Writer.TryComplete()
            ' 送り残し（RESULT など）を少し待ってから閉じる
            Await Task.WhenAny(sendTask, Task.Delay(1000))
            If _ws.State = WebSocketState.Open Then
                Try
                    Using t As New CancellationTokenSource(1000)
                        Await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", t.Token)
                    End Using
                Catch
                End Try
            End If
            cts.Cancel()
            Try
                Await Task.WhenAll(sendTask, watchTask)
            Catch
            End Try
        End Using
        Return failure
    End Function

    Private Async Function ReceiveLoopAsync(lobby As Lobby) As Task
        Dim buffer(16 * 1024 - 1) As Byte
        Dim ms As New IO.MemoryStream()
        Dim tooLarge = False
        While _ws.State = WebSocketState.Open
            Dim r = Await _ws.ReceiveAsync(New ArraySegment(Of Byte)(buffer), CancellationToken.None)
            If r.MessageType = WebSocketMessageType.Close Then
                Try
                    Await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                Catch
                End Try
                Return
            End If
            Interlocked.Exchange(_lastReceiveTicks, Environment.TickCount64)
            If ms.Length + r.Count > MaxMessageBytes Then tooLarge = True Else ms.Write(buffer, 0, r.Count)
            If Not r.EndOfMessage Then Continue While

            Dim isText = r.MessageType = WebSocketMessageType.Text
            Dim text = If(isText AndAlso Not tooLarge, Encoding.UTF8.GetString(ms.GetBuffer(), 0, CInt(ms.Length)), Nothing)
            ms.SetLength(0)
            tooLarge = False
            If text Is Nothing Then
                ServerLog.Write("エラー", ConsoleColor.Red, $"{Label} から受け付けられないデータ（バイナリまたは大きすぎる）")
                Send(Protocol.ErrorMsg(ErrInvalidMessage))
                Continue While
            End If
            If Not lobby.Handle(Me, text) Then Return ' LEAVE
        End While
    End Function

    Private Async Function SendLoopAsync(token As CancellationToken) As Task
        Try
            While Await _outbox.Reader.WaitToReadAsync(token)
                Dim text As String = Nothing
                While _outbox.Reader.TryRead(text)
                    If _ws.State <> WebSocketState.Open AndAlso _ws.State <> WebSocketState.CloseReceived Then Return
                    Dim bytes = Encoding.UTF8.GetBytes(text)
                    Await _ws.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, token)
                End While
            End While
        Catch
            ' 切断済み。受信側で後始末する
        End Try
    End Function

    ''' <summary>15秒間なにも受信しなければ切断とみなす（企画書 6.4 (7)）。</summary>
    Private Async Function WatchTimeoutAsync(token As CancellationToken) As Task
        Try
            While Not token.IsCancellationRequested
                Await Task.Delay(1000, token)
                If Environment.TickCount64 - Interlocked.Read(_lastReceiveTicks) > ReceiveTimeoutMs Then
                    TimedOut = True
                    _ws.Abort()
                    Return
                End If
            End While
        Catch ex As OperationCanceledException
        End Try
    End Function

    ''' <summary>以後の送信を受け付けない（送信キューに残った分は送る）。</summary>
    Public Sub MarkClosing()
        Interlocked.Exchange(_closing, 1)
    End Sub
End Class
