Imports System.Text.Encodings.Web
Imports System.Text.Json

''' <summary>
''' 企画書 6.4 のメッセージ（UTF-8 の JSON、必ず "type" を持つ）の定数と組み立て・読み取り。
''' </summary>
Public Module Protocol
    Public Const DefaultPort As Integer = 8080
    Public Const WsPath As String = "/udon"
    Public Const ReceiveTimeoutMs As Integer = 15000
    Public Const MaxNameLength As Integer = 10
    Public Const MaxRoomLength As Integer = 32
    Public Const MaxMessageBytes As Integer = 64 * 1024
    Public Const Cols As Integer = 6
    Public Const Rows As Integer = 12
    Public Const Countdown As Integer = 3

    ' エラーコード（企画書 6.4 (7)）
    Public Const ErrRoomFull As Integer = 1001
    Public Const ErrInvalidName As Integer = 1002
    Public Const ErrInvalidMessage As Integer = 1003
    Public Const ErrInvalidState As Integer = 1004

    Private ReadOnly _options As New JsonSerializerOptions With {.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping}

    ''' <summary>type と、名前・値を交互に並べた項目から JSON を作る。</summary>
    Public Function Build(type As String, ParamArray fields() As Object) As String
        Dim d As New Dictionary(Of String, Object) From {{"type", type}}
        For i = 0 To fields.Length - 2 Step 2
            d(CStr(fields(i))) = fields(i + 1)
        Next
        Return JsonSerializer.Serialize(d, _options)
    End Function

    Public Function ErrorMsg(code As Integer) As String
        Return Build("ERROR", "code", code, "message", ErrorText(code))
    End Function

    Public Function ErrorText(code As Integer) As String
        Select Case code
            Case ErrRoomFull : Return "room is full"
            Case ErrInvalidName : Return "invalid name"
            Case ErrInvalidMessage : Return "invalid message"
            Case ErrInvalidState : Return "invalid state"
        End Select
        Return "error"
    End Function

    ''' <summary>おじゃまそばの数 = 消去数 × 連鎖数 ÷ 2（切り捨て、最低1個）。</summary>
    Public Function GarbageCount(erased As Integer, chain As Integer) As Integer
        Return Math.Max(1, erased * chain \ 2)
    End Function

    ''' <summary>盤面の文字列が 72 文字で、各文字が 0〜5 か。</summary>
    Public Function IsValidBoard(board As String) As Boolean
        If board Is Nothing OrElse board.Length <> Cols * Rows Then Return False
        For Each ch In board
            If ch < "0"c OrElse ch > "5"c Then Return False
        Next
        Return True
    End Function

    ' ---- 受信したメッセージの項目の読み取り（型が違えば Nothing / False） ----

    Public Function GetString(root As JsonElement, name As String) As String
        Dim v As JsonElement
        If root.TryGetProperty(name, v) AndAlso v.ValueKind = JsonValueKind.String Then Return v.GetString()
        Return Nothing
    End Function

    Public Function TryGetInt(root As JsonElement, name As String, ByRef value As Integer) As Boolean
        Dim v As JsonElement
        If Not root.TryGetProperty(name, v) OrElse v.ValueKind <> JsonValueKind.Number Then Return False
        Return v.TryGetInt32(value)
    End Function

    Public Function TryGetLong(root As JsonElement, name As String, ByRef value As Long) As Boolean
        Dim v As JsonElement
        If Not root.TryGetProperty(name, v) OrElse v.ValueKind <> JsonValueKind.Number Then Return False
        If v.TryGetInt64(value) Then Return True
        Dim d As Double
        If v.TryGetDouble(d) AndAlso Not Double.IsNaN(d) Then
            value = CLng(Math.Clamp(Math.Truncate(d), Long.MinValue, Long.MaxValue))
            Return True
        End If
        Return False
    End Function
End Module
