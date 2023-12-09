using Chess;

namespace steno;

using static System.Text.RegularExpressions.Regex;

internal static class Extensions
{
	private const string Files = "abcdefgh";

	internal static ChessBoard? MakeMove(this ChessBoard board, Move move)
	{
		var fen = board.ToFen();
		board = ChessBoard.LoadFromFen(fen);
		board.AutoEndgameRules = AutoEndgameRules.InsufficientMaterial;
		//	BUG: Sometimes the board we were sent is at endgame and doesn't know it.  This copy does.  Return null to the caller to say no way!
		//	TODO: I don't know if this bug persists, and am not sure which steno was the one where it was first noticed.
		if (board.EndGame is not null)
			return null;
		if (board.Move(move))
			return board;
		throw new ($"Chess library bug: failed to make move {move} on {fen}");
	}

	internal static bool Contains(this ChessBoard board, char fenChar)
		=> board.ToFen().Split()[0].Contains(fenChar);

	internal static string FenWithoutMoveCounts(this ChessBoard board)
		=> string.Join(' ', board.ToFen().Split()[..4]);

	internal static bool PawnAdvancedTo(this ChessBoard board, PieceColor color, char rank)
	{
		for (var rankAsNumber = rank - '0'; rankAsNumber is > 1 and < 8; rankAsNumber += color == PieceColor.White ? 1 : -1)
			if (Files.Any(file =>
						  {
							  var piece = board[$"{file}{rankAsNumber}"];
							  return piece?.Color == color && piece.Type == PieceType.Pawn;
						  }))
				return true;
		return false;
	}

	internal static bool HasPieceOnSquare(this ChessBoard board, char piece, string square)
	{
		foreach (var rank in square.Length is 2 ? square[1..] : "12345678")
			foreach (var squareId in (square.Length is 2 ? square[..1] : Files).Select(file => $"{file}{rank}")
																			   .Where(s => board[s] is not null && square.All(s.Contains)))
			{
				var pieceOnSquare = board[squareId] ?? throw new ();
				var pieceType = $"{pieceOnSquare.Type.AsChar}";
				if (pieceOnSquare.Type == PieceType.Bishop)
					pieceType += squareId.IsLightSquare() ? "l" : "d";
				if (pieceOnSquare.Color == PieceColor.White)
					pieceType = pieceType.ToUpper();
				if (pieceType.Contains(piece))
					return true;
			}
		return false;
	}

	internal static bool SquareIsEmpty(this ChessBoard board, string square)
		=> square.Length is 2
			   ? board[square] is null
			   : "12345678".All(rank => !Files.Select(file => $"{file}{rank}")
											  .All(s => board[s] is null && square.All(s.Contains)));

	internal static bool IsCastling(this Move move, CastleType? side = null)
		=> side is not CastleType.King && move.San?.StartsWith("O-O-O") is true
		|| side is not CastleType.Queen && move.San?.Split('-').Length is 2;

	internal static bool IsPromotion(this Move move, char? mark = null)
		=> move.Parameter?.ShortStr.StartsWith($"={mark}".Replace('l', 'B').ToUpper()) is true;

	internal static string PiecePromoted(this Move move)
	{
		var piece = move.Parameter?.ShortStr;
		if (piece is null)
			return string.Empty;
		piece = $"{piece[1]}{(piece[1] is 'B' ? move.ToLightSquare() ? 'L' : 'D' : null)}";
		return move.Piece.Color == PieceColor.Black ? piece.ToLower() : piece.ToUpper();
	}

	internal static bool IsCapture(this Move move)
		=> move.CapturedPiece is not null;

	internal static string PieceCaptured(this Move move)
	{
		var piece = $"{move.CapturedPiece?.Type.AsChar}";
		if (!piece.Any())
			return piece;
		piece = $"{piece}{(piece[0] is 'B' ? move.ToLightSquare() ? 'L' : 'D' : null)}";
		return move.Piece.Color == PieceColor.Black ? piece.ToLower() : piece.ToUpper();
	}

	private static bool ToLightSquare(this Move move)
		=> $"{move.NewPosition}".IsLightSquare();

	private static bool IsLightSquare(this string square)
		=> square.Sum(static c => c) % 2 is 1;

	internal static bool IsDisambiguatedBy(this Move move, char mark)
		=> move.San?.Split('+')[0].Split('#')[0].Split('=')[0].Split('$')[0].Length > 3 && move.San[1..^2].Contains(mark);

	internal static bool IsDiagonal(this Move move, char mark)
	{
		var lefty = move.OriginalPosition.X < move.NewPosition.X ? move.OriginalPosition : move.NewPosition;
		var other = lefty == move.OriginalPosition ? move.NewPosition : move.OriginalPosition;
		return other.X - lefty.X == (other.Y - lefty.Y) * (mark is '/' ? 1 : -1);
	}

	internal static bool IsToBase(this Move move, char mark)
		=> mark is '^' && move.NewPosition.Y > move.OriginalPosition.Y
		|| mark is 'v' && move.NewPosition.Y < move.OriginalPosition.Y;

	internal static bool IsToSide(this Move move, char mark)
		=> mark is '>' && move.NewPosition.X > move.OriginalPosition.X
		|| mark is '<' && move.NewPosition.X < move.OriginalPosition.X;

	internal static int CountMarksContaining(this List<string> moves, string marks, int countOnlyFirst = default)
		=> (countOnlyFirst is default (int) ? moves : moves.Take(Math.Min(moves.Count, countOnlyFirst)))
		  .Select(static s => Replace(s, "!.", string.Empty))
		  .Count(s => marks.Any(s.Contains));
}
