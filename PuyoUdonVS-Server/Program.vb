Imports System.Net
Imports System.Net.NetworkInformation
Imports System.Net.Sockets
Imports Microsoft.AspNetCore.Builder
Imports Microsoft.AspNetCore.Hosting
Imports Microsoft.AspNetCore.Http
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Logging

''' <summary>
''' ぷようどんVS サーバー。
''' 使い方: PuyoUdonVS-Server [ポート番号(既定 8080)] [--bind 待ち受けIP(既定 0.0.0.0)] [--verbose]
''' ws://&lt;IP&gt;:&lt;ポート&gt;/udon で待ち受ける。
''' </summary>
Module Program
    Private ReadOnly _lobby As New Lobby()

    Function Main(args As String()) As Integer
        System.Console.OutputEncoding = Text.Encoding.UTF8
        System.Console.Title = "ぷようどんVS サーバー"

        Dim port = DefaultPort
        Dim bind = "0.0.0.0"
        Dim i = 0
        While i < args.Length
            Dim a = args(i)
            Select Case a.ToLowerInvariant()
                Case "--verbose", "-v"
                    ServerLog.Verbose = True
                Case "--bind", "-b"
                    If i + 1 < args.Length Then i += 1 : bind = args(i)
                Case "--help", "-h", "/?"
                    ShowUsage()
                    Return 0
                Case Else
                    If (a = "--port" OrElse a = "-p") AndAlso i + 1 < args.Length Then i += 1 : a = args(i)
                    Dim n As Integer
                    If Not Integer.TryParse(a, n) OrElse n < 1 OrElse n > 65535 Then
                        ServerLog.Plain($"ポート番号が正しくありません: {a}", ConsoleColor.Red)
                        ShowUsage()
                        Return 1
                    End If
                    port = n
            End Select
            i += 1
        End While

        Dim bindIp As IPAddress = Nothing
        If Not IPAddress.TryParse(bind, bindIp) Then
            ServerLog.Plain($"待ち受けIPが正しくありません: {bind}", ConsoleColor.Red)
            Return 1
        End If

        Dim builder = WebApplication.CreateBuilder()
        builder.Logging.ClearProviders()
        builder.WebHost.UseKestrel(Sub(o) o.Listen(bindIp, port))
        Dim app = builder.Build()
        app.UseWebSockets(New WebSocketOptions With {.KeepAliveInterval = TimeSpan.FromSeconds(10)})
        app.Map(WsPath, New RequestDelegate(AddressOf HandleAsync))

        Try
            app.StartAsync().GetAwaiter().GetResult()
        Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is SocketException
            ServerLog.Plain($"ポート {port} で待ち受けできませんでした。ほかのアプリが使っていないか確認してください。", ConsoleColor.Red)
            ServerLog.Plain($"（{ex.Message}）", ConsoleColor.DarkGray)
            Return 1
        End Try

        ShowBanner(bindIp, port)
        app.WaitForShutdownAsync().GetAwaiter().GetResult()
        ServerLog.Plain("サーバーを停止しました。", ConsoleColor.Yellow)
        Return 0
    End Function

    ''' <summary>/udon への接続1本ごとに呼ばれる。</summary>
    Private Async Function HandleAsync(ctx As HttpContext) As Task
        If Not ctx.WebSockets.IsWebSocketRequest Then
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest
            Return
        End If
        Dim remote = ctx.Connection.RemoteIpAddress
        If remote IsNot Nothing AndAlso remote.IsIPv4MappedToIPv6 Then remote = remote.MapToIPv4()
        Dim address = If(remote?.ToString(), "?")

        Using ws = Await ctx.WebSockets.AcceptWebSocketAsync()
            Dim c As New ClientConnection(ws, address)
            ServerLog.Write("接続", ConsoleColor.White, $"#{c.Id} {address} が接続しました")
            Dim failure As String = Nothing
            Try
                failure = Await c.RunAsync(_lobby)
            Finally
                _lobby.Disconnect(c)
            End Try
            Dim why = If(c.TimedOut, $"（{ReceiveTimeoutMs \ 1000}秒間応答がないためタイムアウト）", If(failure Is Nothing, "", $"（{failure}）"))
            ServerLog.Write("切断", ConsoleColor.DarkGray, $"{c.Label} の接続が終了しました{why}")
        End Using
    End Function

    Private Sub ShowBanner(bindIp As IPAddress, port As Integer)
        Dim line = New String("="c, 60)
        ServerLog.Plain(line, ConsoleColor.Yellow)
        ServerLog.Plain("  ぷようどんVS サーバーを起動しました", ConsoleColor.Yellow)
        ServerLog.Plain(line, ConsoleColor.Yellow)

        Dim addrs As New List(Of (Ip As String, Note As String))
        If bindIp.Equals(IPAddress.Any) OrElse bindIp.Equals(IPAddress.IPv6Any) Then
            addrs.AddRange(LanAddresses())
            addrs.Add(("127.0.0.1", "このPCから"))
        Else
            addrs.Add((bindIp.ToString(), ""))
        End If

        ServerLog.Plain($"  ポート番号   : {port}", ConsoleColor.White)
        For k = 0 To addrs.Count - 1
            Dim head = If(k = 0, "  IPアドレス   : ", "                 ")
            Dim note = If(addrs(k).Note = "", "", $"  ({addrs(k).Note})")
            ServerLog.Plain($"{head}{addrs(k).Ip}{note}", ConsoleColor.White)
        Next
        Dim ipForUrl = addrs(0).Ip
        If ipForUrl.Contains(":"c) Then ipForUrl = $"[{ipForUrl}]"
        ServerLog.Plain($"  接続先       : ws://{ipForUrl}:{port}{WsPath}", ConsoleColor.White)
        ServerLog.Plain("  クライアントには上の IPアドレス と ポート番号 を入力してください。", ConsoleColor.Gray)
        ServerLog.Plain("  Ctrl+C で停止します。" & If(ServerLog.Verbose, "（詳細表示: ON）", "  --verbose で PIECE / STATE / PING も表示します。"), ConsoleColor.DarkGray)
        ServerLog.Plain(line, ConsoleColor.Yellow)
    End Sub

    ''' <summary>このPCの LAN 側 IPv4 アドレス（稼働中のアダプターのみ）。</summary>
    Private Function LanAddresses() As List(Of (Ip As String, Note As String))
        Dim result As New List(Of (Ip As String, Note As String))
        Try
            For Each nic In NetworkInterface.GetAllNetworkInterfaces()
                If nic.OperationalStatus <> OperationalStatus.Up Then Continue For
                If nic.NetworkInterfaceType = NetworkInterfaceType.Loopback OrElse nic.NetworkInterfaceType = NetworkInterfaceType.Tunnel Then Continue For
                For Each ua In nic.GetIPProperties().UnicastAddresses
                    Dim ip = ua.Address
                    If ip.AddressFamily <> AddressFamily.InterNetwork Then Continue For
                    If ip.ToString().StartsWith("169.254.") Then Continue For ' 自動割り当て（未接続）
                    result.Add((ip.ToString(), nic.Name))
                Next
            Next
        Catch ex As NetworkInformationException
        End Try
        Return result
    End Function

    Private Sub ShowUsage()
        ServerLog.Plain("使い方: PuyoUdonVS-Server [ポート番号] [--bind 待ち受けIP] [--verbose]")
        ServerLog.Plain($"  ポート番号   既定 {DefaultPort}")
        ServerLog.Plain("  --bind       待ち受けるIP（既定 0.0.0.0 = すべて）")
        ServerLog.Plain("  --verbose    PIECE / STATE / PING など頻繁なメッセージも表示する")
    End Sub
End Module
