''' <summary>2人で対戦する1つのルーム。Lobby のロック内でだけ操作する。</summary>
Public NotInheritable Class Room
    Public ReadOnly Id As String
    ''' <summary>自動マッチングで作ったルームか。</summary>
    Public ReadOnly IsAuto As Boolean
    ''' <summary>Players(0) が P1、Players(1) が P2。</summary>
    Public ReadOnly Players(1) As ClientConnection
    ''' <summary>START を送ってから RESULT を送るまで true。</summary>
    Public Playing As Boolean
    ''' <summary>このルームで行った対戦の回数。</summary>
    Public Games As Integer
    Public Seed As Integer

    Public Sub New(id As String, isAuto As Boolean)
        Me.Id = id
        Me.IsAuto = isAuto
    End Sub

    Public ReadOnly Property Count As Integer
        Get
            Return If(Players(0) Is Nothing, 0, 1) + If(Players(1) Is Nothing, 0, 1)
        End Get
    End Property

    Public Function Other(c As ClientConnection) As ClientConnection
        Return If(Players(0) Is c, Players(1), Players(0))
    End Function

    ''' <summary>ログ用の表記。例: ルーム「udon1」</summary>
    Public ReadOnly Property Label As String
        Get
            Return If(IsAuto, $"ルーム「{Id}」(自動)", $"ルーム「{Id}」")
        End Get
    End Property
End Class
