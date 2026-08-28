Imports System
Imports System.ComponentModel
Imports System.IO
Imports System.Linq.Expressions
Imports System.Numerics
Imports System.Runtime.CompilerServices
Imports System.Security.Cryptography
Imports System.Threading

'Condensed Magic and Mask Info for efficient OOP storage. The exported data is a serialised array of 'MagicInfo', one structure for each square.
Public Structure MagicInfo
    Public Magic As UInt64 '64-bit unsigned magic number, with 87.5% set sparsity and always containing at least 6 set bits.
    Public Shift As Integer 'Number of places to shift the "Magic * Blocker Mask" key, to produce an entry in the array of legal moves.
    Public MoveMap() As UInt64 'Hashed array of legal moves, containing the movement map for a rook at each location with specific blocker pattern.
End Structure
Public Structure MaskInfo
    Public PieceMask As UInt64
    Public MoveMask As UInt64
End Structure

'Uses the same random algorithm as System.Random, but tweaked so it generates Uint64!
Public Class Xoshiro256
    Private S0, S1, S2, S3 As UInt64
    Public Sub New(ByVal Seed As UInt64)
        S0 = NextSplitMix(Seed)
        S1 = NextSplitMix(Seed)
        S2 = NextSplitMix(Seed)
        S3 = NextSplitMix(Seed)
    End Sub

    Private Shared Function NextSplitMix(ByRef State As UInt64) As UInt64
        State += 11400714819323198485UL
        Dim z As UInt64 = State
        z = (z Xor (z >> 30)) * 13787848793156543929UL
        z = (z Xor (z >> 27)) * 10723151780598845935UL
        Return z Xor (z >> 31)
    End Function

    <MethodImpl(MethodImplOptions.AggressiveInlining)>
    Public Function NextUInt64() As UInt64
        Dim RNDValue As UInt64 = 9UL * (S1 * 5UL << 7) Or (S1 * 5UL >> (64 - 7))
        'Scrambles states.
        Dim Temp As UInt64 = S1 << 17
        S2 = S2 Xor S0
        S3 = S3 Xor S1
        S1 = S1 Xor S2
        S0 = S0 Xor S3
        S2 = S2 Xor Temp
        S3 = (S3 << 45) Or (S3 >> (64 - 45))
        Return RNDValue
    End Function

    Public Function GenerateMagicCandidate() As UInt64
        'Thanks to https://www.reddit.com/r/chessprogramming/comments/19djpcl/magic_number_generation/!
        Dim Magic As UInt64
        Do
            Magic = NextUInt64() And NextUInt64() And NextUInt64()
        Loop Until BitOperations.PopCount(Magic) >= 6
        Return Magic
    End Function
End Class

Module Program
    Private ABORT As Boolean
    Dim Timer As New Stopwatch

    'Console Stats.
    Private MagicNoProcessed(63) As UInt64
    Private RookSquaresDone(63) As Boolean
    Private BishopSquaresDone(63) As Boolean

    'Global Data Stored by program, to output to file (pickle).
    Private MagicRookInfo(63) As MagicInfo
    Private MagicBishopInfo(63) As MagicInfo
    Private FinishOnOptimality As Boolean

    Sub Main(args As String())
        Console.ForegroundColor = ConsoleColor.White
        Console.Write("Terminate Search upon Reaching Optimal Storage? [y/n] ")
        FinishOnOptimality = Console.ReadKey().Key = ConsoleKey.Y

        Console.ForegroundColor = ConsoleColor.Blue
        Console.Write(vbCrLf & "Precomputing Rook Legal Moves... ")
        Timer.Start()
        Dim RookPieceMasks(63)() As MaskInfo
        For n = 0 To 63
            Dim BaseTFTable As UInt64 = CreateRookMoveMap(n, 0UL, False)
            RookPieceMasks(n) = CreatePieceMasks(BaseTFTable, n, True)
        Next
        Timer.Stop()
        Console.ForegroundColor = ConsoleColor.Green
        Console.WriteLine("DONE!")

        Console.ForegroundColor = ConsoleColor.Blue
        Console.Write("Precomputing Bishop Legal Moves... ")
        Timer.Restart()
        Dim BishopPieceMasks(63)() As MaskInfo
        For n = 0 To 63
            Dim BaseTFTable As UInt64 = CreateBishopMoveMap(n, 0UL, False)
            BishopPieceMasks(n) = CreatePieceMasks(BaseTFTable, n, False)
        Next
        Timer.Stop()
        Console.ForegroundColor = ConsoleColor.Green
        Console.WriteLine("DONE!")
        Console.ForegroundColor = ConsoleColor.White
        Console.WriteLine($"Finished in {Math.Round(Timer.Elapsed.TotalMilliseconds, 3)}ms. Ready to try some Magic Numbers! :D" & New String(ControlChars.Lf, 13))

        'DEBUG:
        'Dim PieceLocation As Integer = 20
        'Dim TestArray As MaskInfo() = CreatePieceMasks(CreateBishopMoveMap(PieceLocation, 0UL, False), PieceLocation, False)
        'For n = 0 To 99
        '    OutputBitMaskToConsole(TestArray(n).PieceMask, PiecePosition:=PieceLocation)
        '    OutputBitMaskToConsole(TestArray(n).MoveMask, PiecePosition:=PieceLocation)
        'Next

        Console.Write("Press ENTER to begin... ")
        Console.ReadLine()

        'Allows the console updating to run in its own thread, for smooth displaying.
        Dim ConsoleThread As New Thread(Sub()
                                            While Not ABORT
                                                Thread.Sleep(100)
                                                UpdateConsole()
                                                If Console.KeyAvailable Then ABORT = True
                                            End While
                                        End Sub) With {.IsBackground = True}

        ConsoleThread.Start()

        'Initiates all shared structures that the threads will access.
        Dim RNDGens(63) As Xoshiro256
        Dim TempRookMaps(63)() As UInt64
        Dim RookGens(63)() As Integer
        Dim TempBishopMaps(63)() As UInt64
        Dim BishopGens(63)() As Integer
        For n = 0 To 63
            'Tries a large upper estimate for the ideal fancy hashed array size. Uses this to initialise array structures (with a guess 50% of the size of this - 1 extra shift).
            Dim RookSizeUpperEstimate As Integer = 14
            Dim BishopSizeUpperEstimate As Integer = 11

            MagicRookInfo(n).Shift = 64 - RookSizeUpperEstimate
            TempRookMaps(n) = New UInt64((1 << (RookSizeUpperEstimate - 1)) - 1) {}
            MagicBishopInfo(n).Shift = 64 - BishopSizeUpperEstimate
            TempBishopMaps(n) = New UInt64((1 << (BishopSizeUpperEstimate - 1)) - 1) {}

            RookGens(n) = New Integer(TempRookMaps(n).Length - 1) {}
            BishopGens(n) = New Integer(TempBishopMaps(n).Length - 1) {}
            RNDGens(n) = New Xoshiro256(1070372UL + (CULng(n) * 7046029254386353131UL)) 'Random-ish seed (ie: I hit random numbers)
        Next

        Dim Options As New ParallelOptions With {.MaxDegreeOfParallelism = Environment.ProcessorCount - 1} 'The other thread for the UI.
        Timer.Restart()
        Parallel.For(0, Environment.ProcessorCount - 1, Options, Sub(ID)
                                                                     'Fancy magic bitboards: Constantly searching for more and more optimal magic numbers (even
                                                                     'after we've found them for all 64 squares). Does this so that we can reduce the final
                                                                     'hash array memory footprint.
                                                                     While Not ABORT
                                                                         For n = ID To 63 Step Environment.ProcessorCount - 1
                                                                             'Allocates all data structures to the thread. Runs 5000 iterations until moving onto the next square (for now)
                                                                             Dim TempRookMap As ULong() = TempRookMaps(n)
                                                                             Dim TempBishopMap As ULong() = TempBishopMaps(n)

                                                                             Dim RookGenerations As Integer() = RookGens(n)
                                                                             Dim BishopGenerations As Integer() = BishopGens(n)
                                                                             Dim RNDGen As Xoshiro256 = RNDGens(n)
                                                                             Dim LocalMagicProcessed As UInt64 = MagicNoProcessed(n)

                                                                             For Batch = 1 To 5000
                                                                                 'Attempts to fill the arrays with 1 extra shift, to reduce size.
                                                                                 Dim TargetShift As Integer = MagicRookInfo(n).Shift + 1
                                                                                 Dim Magic As UInt64 = RNDGen.GenerateMagicCandidate()
                                                                                 LocalMagicProcessed += 1UL
                                                                                 Dim FoundCollision As Boolean = False
                                                                                 For i = 0 To RookPieceMasks(n).Count - 1
                                                                                     'Checks hashed array against the key of Magic Number * Blocker Map.
                                                                                     Dim Key As Integer = CInt((Magic * RookPieceMasks(n)(i).PieceMask) >> TargetShift)
                                                                                     If TempRookMap(Key) = 0 OrElse RookGenerations(Key) < LocalMagicProcessed Then
                                                                                         'Space is empty (or was filled by a previous search). Fill it with the relevant rook legal moves.
                                                                                         TempRookMap(Key) = RookPieceMasks(n)(i).MoveMask
                                                                                         RookGenerations(Key) = CInt(LocalMagicProcessed)
                                                                                     ElseIf TempRookMap(Key) <> RookPieceMasks(n)(i).MoveMask Then
                                                                                         'Map entry is being used by a different legal move mask - try again with a different magic number.
                                                                                         FoundCollision = True
                                                                                         Exit For
                                                                                     End If
                                                                                 Next
                                                                                 If Not FoundCollision Then
                                                                                     'Saves the new magic info globally.
                                                                                     MagicRookInfo(n).Magic = Magic
                                                                                     MagicRookInfo(n).Shift = TargetShift
                                                                                     Dim SavedMap(TempRookMap.Length - 1) As UInt64
                                                                                     Array.Copy(TempRookMap, SavedMap, TempRookMap.Length)
                                                                                     MagicRookInfo(n).MoveMap = SavedMap

                                                                                     'Initialises the map 50% smaller (1 extra shift), to see if we can go lower.
                                                                                     ReDim TempRookMap((1 << (63 - TargetShift)) - 1)
                                                                                     TempRookMaps(n) = TempRookMap
                                                                                     If Not RookSquaresDone(n) Then RookSquaresDone(n) = True
                                                                                 End If

                                                                                 'Similar code for the bishop magic number generation.
                                                                                 TargetShift = MagicBishopInfo(n).Shift + 1
                                                                                 Magic = RNDGen.GenerateMagicCandidate()
                                                                                 LocalMagicProcessed += 1UL
                                                                                 FoundCollision = False
                                                                                 For i = 0 To BishopPieceMasks(n).Count - 1
                                                                                     Dim Key As Integer = CInt((Magic * BishopPieceMasks(n)(i).PieceMask) >> TargetShift)
                                                                                     If TempBishopMap(Key) = 0 OrElse BishopGenerations(Key) < LocalMagicProcessed Then
                                                                                         TempBishopMap(Key) = BishopPieceMasks(n)(i).MoveMask
                                                                                         BishopGenerations(Key) = CInt(LocalMagicProcessed)
                                                                                     ElseIf TempBishopMap(Key) <> BishopPieceMasks(n)(i).MoveMask Then
                                                                                         FoundCollision = True
                                                                                         Exit For
                                                                                     End If
                                                                                 Next
                                                                                 If Not FoundCollision Then
                                                                                     MagicBishopInfo(n).Magic = Magic
                                                                                     MagicBishopInfo(n).Shift = TargetShift
                                                                                     Dim SavedMap(TempBishopMap.Length - 1) As UInt64
                                                                                     Array.Copy(TempBishopMap, SavedMap, TempBishopMap.Length)
                                                                                     MagicBishopInfo(n).MoveMap = SavedMap

                                                                                     ReDim TempBishopMap((1 << (63 - TargetShift)) - 1)
                                                                                     TempBishopMaps(n) = TempBishopMap
                                                                                     If Not BishopSquaresDone(n) Then BishopSquaresDone(n) = True
                                                                                 End If

                                                                             Next
                                                                             MagicNoProcessed(n) = LocalMagicProcessed
                                                                         Next
                                                                     End While
                                                                 End Sub)

        Timer.Stop()
        UpdateConsole()
        Console.ForegroundColor = ConsoleColor.DarkRed
        Console.WriteLine("Terminated Search Early from Reaching Optimal Storage.")
        Console.ForegroundColor = ConsoleColor.Green
        Console.WriteLine(vbCrLf & $"Finished Searching in {Math.Round(Timer.Elapsed.TotalSeconds, 2)}s.")

        Console.ForegroundColor = ConsoleColor.Blue
        Console.Write("Saving Magic Numbers, Shifts, and Move Maps to 'MagicData.bin'... ")
        Timer.Restart()
        'Saves as binary file by serialising the array.
        Using FS As New FileStream("MagicData.bit", FileMode.Create, FileAccess.Write)
            Using BW As New BinaryWriter(FS)
                For Each MagicData In {MagicRookInfo, MagicBishopInfo}
                    For i = 0 To 63
                        BW.Write(MagicData(i).Magic)
                        BW.Write(MagicData(i).Shift)
                        BW.Write(MagicData(i).MoveMap.Length)
                        For Each Move In MagicData(i).MoveMap
                            BW.Write(Move)
                        Next
                    Next
                Next
            End Using
        End Using
        Timer.Stop()
        Console.ForegroundColor = ConsoleColor.Green
        Console.WriteLine("DONE!")
        Console.ForegroundColor = ConsoleColor.White
        Console.WriteLine($"Finished in {Math.Round(Timer.Elapsed.TotalMilliseconds)}ms. Bye bye! :)" & vbCrLf)
        Console.ReadLine()
    End Sub


    'Live updating of the console with live search statistics.
    Private Sub UpdateConsole()
        Dim MinRookShift As Integer = MagicRookInfo.Min(Function(item) item.Shift)
        Dim MaxRookShift As Integer = MagicRookInfo.Max(Function(item) item.Shift)
        Dim MinBishopShift As Integer = MagicBishopInfo.Min(Function(item) item.Shift)
        Dim MaxBishopShift As Integer = MagicBishopInfo.Max(Function(item) item.Shift)

        Dim TotalRookSize, TotalBishopSize As Double
        For n = 0 To 63
            TotalRookSize += 8 * (1 << (64 - MagicRookInfo(n).Shift)) / 1024.0
            TotalBishopSize += 8 * (1 << (64 - MagicBishopInfo(n).Shift)) / 1024.0
        Next

        Dim TotalRookSquares As Integer = RookSquaresDone.Count(Function(b) b)
        Dim TotalBishopSquares As Integer = BishopSquaresDone.Count(Function(b) b)
        Dim TotalMagicNumbersProcessed As UInt64 = MagicNoProcessed.Aggregate(0UL, Function(acc, x) acc + x)

        Console.SetCursorPosition(0, Console.CursorTop - 13)
        Console.ForegroundColor = ConsoleColor.Red
        Console.WriteLine("-----------PRESS-ENTER-TO-STOP-SEARCHING-----------")
        Console.ForegroundColor = ConsoleColor.White
        Console.WriteLine($"Processed {Ansi.Yellow}{TotalMagicNumbersProcessed.ToString("N0")}{Ansi.White} Magic Numbers ({Ansi.Yellow}{Math.Round(TotalMagicNumbersProcessed / Timer.Elapsed.TotalSeconds).ToString("N0")}Nps{Ansi.White}).     {vbCrLf}")

        'Displays info colouring based on known theoretical values (red = not finished computing, green = finished computing, searching for 
        'more optimal magic numbers, gold = found optimal values).
        Dim RookSizeColour As String = If(TotalRookSquares = 64, If(Math.Round(TotalRookSize, 3) = 800, Ansi.DarkYellow, Ansi.Green), Ansi.Red)
        Dim BishopSizeColour As String = If(TotalBishopSquares = 64, If(Math.Round(TotalBishopSize, 3) = 41, Ansi.DarkYellow, Ansi.Green), Ansi.Red)
        If FinishOnOptimality AndAlso RookSizeColour = Ansi.DarkYellow AndAlso BishopSizeColour = Ansi.DarkYellow Then ABORT = True

        Console.ForegroundColor = ConsoleColor.Cyan
        Console.WriteLine("Rook Magic Info:")
        Console.ForegroundColor = ConsoleColor.White
        Console.WriteLine($"  Squares Found: {If(TotalRookSquares < 64, Ansi.Red, Ansi.Green)}{TotalRookSquares}{Ansi.White}     ")
        Console.WriteLine($"  Bit Shift Range: [{RookSizeColour}{MinRookShift}, {MaxRookShift}{Ansi.White}] (Bits: {RookSizeColour}{64 - MaxRookShift} - {64 - MinRookShift}{Ansi.White})     ")
        Console.WriteLine($"  Total Size: {RookSizeColour}{TotalRookSize:F2}kB{Ansi.White} ({RookSizeColour}{Math.Round(TotalRookSize / TotalRookSquares, 3)}kB{Ansi.White}/sq)     " & vbCrLf)

        Console.ForegroundColor = ConsoleColor.Cyan
        Console.WriteLine("Bishop Magic Info:")
        Console.ForegroundColor = ConsoleColor.White
        Console.WriteLine($"  Squares Found: {If(TotalBishopSquares < 64, Ansi.Red, Ansi.Green)}{TotalBishopSquares}{Ansi.White}     ")
        Console.WriteLine($"  Bit Shift Range: [{BishopSizeColour}{MinBishopShift}, {MaxBishopShift}{Ansi.White}] (Bits: {BishopSizeColour}{64 - MaxBishopShift} - {64 - MinBishopShift}{Ansi.White})     ")
        Console.WriteLine($"  Total Size: {BishopSizeColour}{TotalBishopSize:F2}kB{Ansi.White} ({BishopSizeColour}{Math.Round(TotalBishopSize / TotalBishopSquares, 3)}kB{Ansi.White}/sq)     ")
        Console.ForegroundColor = ConsoleColor.Red
        Console.WriteLine(New String("-"c, 51))
    End Sub



    <MethodImpl(MethodImplOptions.AggressiveInlining)>
    Private Function Flatten2DBoardIndex(ByVal x As Integer, ByVal y As Integer) As Integer
        Return 8 * y + x
    End Function

    'Given a movement mask, calculates the full set of possible blocker locations, along with the rook legal moves that they produce,
    'then saves them to a list.
    Public Function CreatePieceMasks(ByVal TFTable As UInt64, ByVal Square As Integer, ByVal IsPieceRook As Boolean) As MaskInfo()
        'Adds full list of 1s to a list.
        Dim IndexList As New List(Of Integer)
        Do
            Dim Index As Integer = BitOperations.TrailingZeroCount(TFTable)
            TFTable = TFTable Xor (1UL << Index)
            IndexList.Add(Index)
        Loop Until TFTable = 0

        Dim Masks(CInt(2 ^ IndexList.Count)) As MaskInfo
        For n = 0 To Masks.Length - 1
            Dim TempMask As UInt64 = 0
            'Isolates each bit in the count, then pushes it into a 1 slot in the mask.
            For i = 0 To IndexList.Count - 1
                If ((n >> i) And 1) = 1 Then TempMask = TempMask Or (1UL << IndexList(i))
            Next
            Masks(n).PieceMask = TempMask

            'Creates the full set of legal moves for the rook at that location.
            Masks(n).MoveMask = If(IsPieceRook, CreateRookMoveMap(Square, TempMask, True), CreateBishopMoveMap(Square, TempMask, True))
        Next
        Return Masks
    End Function

    'Given a bitmask of blocker locations (and the rook square), calculates the bitmap of the rook's legal moves.
    'Used as array entries for the hashing, to resolve collisions. Also creates a TFTable if PieceMask = 0 and IterateToEdge = True.
    Public Function CreateRookMoveMap(ByVal Square As Integer, ByVal PieceMask As UInt64, Optional ByVal IterateToEdge As Boolean = False) As UInt64
        Dim TFTable, ShiftedIndex As UInt64
        'Simulates a rook on that square, and casts rays in all 4 directions, adding 1s to the mask.
        Dim y As Integer = Square \ 8
        Dim x As Integer = Square Mod 8
        For n = x + 1 To If(IterateToEdge, 7, 6)
            ShiftedIndex = 1UL << Flatten2DBoardIndex(n, y)
            TFTable = TFTable Or ShiftedIndex
            If (PieceMask And ShiftedIndex) > 0 Then Exit For
        Next
        For n = x - 1 To If(IterateToEdge, 0, 1) Step -1
            ShiftedIndex = 1UL << Flatten2DBoardIndex(n, y)
            TFTable = TFTable Or ShiftedIndex
            If (PieceMask And ShiftedIndex) > 0 Then Exit For
        Next
        For n = y + 1 To If(IterateToEdge, 7, 6)
            ShiftedIndex = 1UL << Flatten2DBoardIndex(x, n)
            TFTable = TFTable Or ShiftedIndex
            If (PieceMask And ShiftedIndex) > 0 Then Exit For
        Next
        For n = y - 1 To If(IterateToEdge, 0, 1) Step -1
            ShiftedIndex = 1UL << Flatten2DBoardIndex(x, n)
            TFTable = TFTable Or ShiftedIndex
            If (PieceMask And ShiftedIndex) > 0 Then Exit For
        Next
        Return TFTable
    End Function


    'Exact same code, but for bishops moves.
    Public Function CreateBishopMoveMap(ByVal Square As Integer, ByVal PieceMask As UInt64, Optional ByVal IterateToEdge As Boolean = False) As UInt64
        Dim TFTable, ShiftedIndex As UInt64
        Dim y As Integer = Square \ 8
        Dim x As Integer = Square Mod 8
        Dim n, m As Integer

        n = x + 1
        m = y + 1
        Do While n <= If(IterateToEdge, 7, 6) AndAlso m <= If(IterateToEdge, 7, 6)
            ShiftedIndex = 1UL << Flatten2DBoardIndex(n, m)
            TFTable = TFTable Or ShiftedIndex
            If (PieceMask And ShiftedIndex) > 0 Then Exit Do
            n += 1
            m += 1
        Loop
        n = x + 1
        m = y - 1
        Do While n <= If(IterateToEdge, 7, 6) AndAlso m >= If(IterateToEdge, 0, 1)
            ShiftedIndex = 1UL << Flatten2DBoardIndex(n, m)
            TFTable = TFTable Or ShiftedIndex
            If (PieceMask And ShiftedIndex) > 0 Then Exit Do
            n += 1
            m -= 1
        Loop
        n = x - 1
        m = y - 1
        Do While n >= If(IterateToEdge, 0, 1) AndAlso m >= If(IterateToEdge, 0, 1)
            ShiftedIndex = 1UL << Flatten2DBoardIndex(n, m)
            TFTable = TFTable Or ShiftedIndex
            If (PieceMask And ShiftedIndex) > 0 Then Exit Do
            n -= 1
            m -= 1
        Loop
        n = x - 1
        m = y + 1
        Do While n >= If(IterateToEdge, 0, 1) AndAlso m <= If(IterateToEdge, 7, 6)
            ShiftedIndex = 1UL << Flatten2DBoardIndex(n, m)
            TFTable = TFTable Or ShiftedIndex
            If (PieceMask And ShiftedIndex) > 0 Then Exit Do
            n -= 1
            m += 1
        Loop
        Return TFTable
    End Function



    'Debug tool for testing bitboards.
    Private Sub OutputBitMaskToConsole(ByVal Mask As UInt64, Optional ByVal OutputAsGrid As Boolean = True, Optional ByVal PiecePosition As Integer = -1)
        'a8 is the *last* bit in the binary - turn the binary into a string, and reverse it.
        Dim BinaryMask As String = String.Join("", BitConverter.GetBytes(CULng(Mask)).Reverse().Select(Function(b) Convert.ToString(b, 2).PadLeft(8, "0"c)))
        Dim Counter As Integer
        For i = 63 To 0 Step -1
            'Outputs red for 0s, green for 1s, and an optional blue if we hit a pre-pinged piece position.
            If i = 63 - PiecePosition Then
                Console.ForegroundColor = ConsoleColor.Cyan
            ElseIf BinaryMask(i) = "1"c Then
                Console.ForegroundColor = ConsoleColor.Green
            Else
                Console.ForegroundColor = ConsoleColor.Red
            End If
            Console.Write(BinaryMask(i))
            Counter += 1
            If OutputAsGrid AndAlso Counter = 8 Then Counter = 0 : Console.WriteLine()
        Next
        Console.ResetColor()
        Console.WriteLine()
    End Sub

End Module

Public Structure Ansi
    Public Const Black As String = ChrW(27) & "[30m"
    Public Const DarkRed As String = ChrW(27) & "[31m"
    Public Const DarkGreen As String = ChrW(27) & "[32m"
    Public Const DarkYellow As String = ChrW(27) & "[33m"
    Public Const DarkBlue As String = ChrW(27) & "[34m"
    Public Const DarkMagenta As String = ChrW(27) & "[35m"
    Public Const DarkCyan As String = ChrW(27) & "[36m"
    Public Const Gray As String = ChrW(27) & "[37m"

    Public Const DarkGray As String = ChrW(27) & "[90m"
    Public Const Red As String = ChrW(27) & "[91m"
    Public Const Green As String = ChrW(27) & "[92m"
    Public Const Yellow As String = ChrW(27) & "[93m"
    Public Const Blue As String = ChrW(27) & "[94m"
    Public Const Magenta As String = ChrW(27) & "[95m"
    Public Const Cyan As String = ChrW(27) & "[96m"
    Public Const White As String = ChrW(27) & "[97m"
End Structure