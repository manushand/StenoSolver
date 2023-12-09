using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Chess;
using Newtonsoft.Json;

namespace steno;

using static CompressionLevel;
using static Int32;
using static String;
using static Array;
using static Regex;
using static JsonConvert;
using static Solver.MessageType;
using static Solver.Lingo;

public sealed class Solver
{
	#region Public types, fields, and properties

	public const string VersionNumber = "0.12";

	public enum Lingo : byte
	{
		Extended = default,
		Classic,
		PGN,
		E = Extended,
		C = Classic,
		P = PGN
	}

	public enum MessageType : byte
	{
		Status = default,
		InProgress,
		Success,
		Error,
		Abort
	}

	public delegate void MessageHandler(string messageText, MessageType messageType = Status);

	public string Vocabulary
	{
		get => $"{_lingo}".ToUpper();
		set
		{
			var valid = Enum.TryParse(value, true, out _lingo);
			Report(valid
					   ? $"VOCABULARY SET TO {_lingo switch { C => "CLASSIC", E => "EXTENDED", _ => "PGN" }}"
					   : "INVALID VOCABULARY; MUST BE CLASSIC, EXTENDED, OR PGN (C, E, or P)",
				   valid ? Success : Error);
		}
	}

	public bool DisplayPositions { get; private set; }

	public int MaxSolutionsToList { get; private set; }

	public string MaxPositionsToExamine
	{
		get => Replace($"{_maxPositionsToExamine}", "000$", "K").Replace("000K", "M").Replace("000M", "B");
		set
		{
			var numeric = value.ToUpper().Trim().Replace("MAX", $"{MaxSettingsValue}");
			if (numeric[..^1].All(char.IsDigit)
			 && TryParse(numeric.Replace("K", "000").Replace("M", "000000").Replace("B", "000000000"), out var number)
			 && number is > 0 and <= MaxSettingsValue)
				Report($"WILL EXAMINE A MAXIMUM OF {_maxPositionsToExamine = number:N0} POSITIONS PER STENO MARK", Success);
			else
				Report($"INVALID LIMIT FOR POSITIONS TO EXAMINE: {value}", Error);
		}
	}

	public int MaxCooksToKeep
	{
		get => _maxCooksToKeep;
		set
		{
			if (value is >= 1 and <= MaxSettingsValue)
				Report($"WILL RETAIN A MAXIMUM OF {_maxCooksToKeep = value:N0} COOK{(_maxCooksToKeep is 1 ? null : "S")} PER POSITION", Success);
			else
				Report("INVALID LIMIT FOR RETAINED COOKS (MUST BE 1 OR MORE)", Error);
		}
	}

	public int MaxSolverTasks
	{
		get => _maxSolverTasks;
		set
		{
			if (value > 0)
				Report($"CONCURRENT TASK LIMIT SET TO {_maxSolverTasks = value}", Success);
			else
				Report("INVALID CONCURRENT TASK LIMIT", Error);
		}
	}

	public string OutputFile
	{
		get => _outputFile;
		set
		{
			if (value.Any())
				try
				{
					File.AppendAllText(value, string.Empty);
				}
				catch
				{
					Report($"INVALID FILE NAME {value}", Error);
					return;
				}
			_outputFile = value;
		}
	}

	public string StartFen
	{
		get
		{
			const string pawns = "pp" + "pp" + "pp" + "pp";
			if (_startFen.Length > 8)
				return _startFen;
			var backRank = _startFen.Any() ? _startFen : "RN" + "BQ" + "KB" + "NR";
			return $"{backRank.ToLower()}/{pawns}/8/8/8/8/{pawns.ToUpper()}/{backRank} w KQkq - 0 1";
		}
		set
		{
			var fen = value;
			switch (fen.Length)
			{
			case 0:
				_startFen = string.Empty;
				Report("START FEN RESET", Success);
				break;
			case 8 when new string(fen.ToUpper().Order().ToArray()) is "BBK" + "NN" + "QRR":
				//	This will be the back-rank for a Fischer game starting from move 1
				fen = fen.ToUpper();
				Report($"START BACK RANK SET TO {StartFen}", Success);
				_startFen = fen is "RNB" + "QK" + "BNR" ? string.Empty : fen;
				return;
			default:
				//	We better have been given the first three or more parts of a valid FEN
				var words = fen.Split();
				if (words.Length < 4)
					fen += " -";
				if (words.Length < 5)
					fen += " 0";
				if (words.Length < 6)
					fen += " 1";
				var valid = ValidateFen(fen);
				Report(valid
						   ? $"START FEN SET TO {_startFen = fen}"
						   : "INVALID FEN",
					   valid ? Success : Error);
				break;
			}
		}
	}

	public byte[] Checkpoint
	{
		get => _checkpoint;
		set
		{
			_checkpoint = value;
			_checkpointStenoData.Clear();
			_checkpointPositions.Clear();
			if (!value.Any())
				return;

			var checkpoint = Decompress(value);
			var savedData = checkpoint.Split(SavedDataSeparator) ?? throw new ();
			_checkpointPositions = DeserializeObject<Dictionary<string, Position>>(savedData[0]) ?? throw new ();
			var turn = _checkpointPositions.Values.First().MoveSets.Last().Moves.TrimEnd().Count(static c => c is ' ') / 2 + 2;
			foreach (var (fen, position) in _checkpointPositions)
				position.SetBoard(fen, turn);

			_checkpointStenoData.AddRange(DeserializeObject<List<StenoData>>(savedData[1]) ?? throw new ());
		}
	}

	public string SavedSteno => Join(' ', _checkpointStenoData.Select(static s => $"{s.Marks}{s.Conditions}"));
	public int SavedPositionsCount => _checkpointPositions.Count;

	#endregion

	#region Constructor

	public Solver(MessageHandler messageHandler,
				  string vocabulary = nameof (Extended),
				  string maxPositionsToExamine = "200K",
				  int maxCooksToKeep = 1,
				  int maxSolutionsToList = 2,
				  bool displayPositions = false,
				  bool showMetaMarks = true,
				  bool allowChunking = true)
	{
		//	Until we have made the assignments to the properties, have MessageHandler do
		//	nothing because those assignments generate messages we don't need to send.
		Vocabulary = vocabulary;
		MaxPositionsToExamine = maxPositionsToExamine;
		MaxSolutionsToList = maxSolutionsToList;
		DisplayPositions = displayPositions;
		_showMetaMarks = showMetaMarks;
		_maxCooksToKeep = maxCooksToKeep;
		_allowChunking = allowChunking;
		//	...and now we can assign the MessageHandler to handle all the reports.
		_report = messageHandler;
		//	If the user of this Solver wants to use a non-standard-start position, it's up to him to first set StartFen
	}

	#endregion

	#region Public methods

	public void Solve(string steno, string? startFen = null)
	{
		if (startFen is not null && startFen != _startFen)
		{
			StartFen = startFen;
			if (StartFen != _startFen)
				return;
		}

		_stenoData.Clear();
		_steno = steno;

		if (!ValidateSteno())
			return;

		try
		{
			RunSolve();
		}
		catch (Exception ex) when (ex is TaskCanceledException
								|| (ex as AggregateException)?.InnerExceptions.All(static x => x is TaskCanceledException) is true) { }
		catch (Exception ex)
		{
			Report($"\nSORRY; THE SOLVER CHOKED AND DIED: {ex.Message}", Error);
		}
		if (SolveAborted)
			_abortSolveTokenSource = new ();
	}

	public void Abort()
		=> Report("\nSOLVE ABORTED", MessageType.Abort);

	public void SetMaxSolutions(int count, bool display)
	{
		var valid = count is 0 or > 1;
		if (valid)
		{
			DisplayPositions = display;
			MaxSolutionsToList = count;
			Report(count is 0
					   ? "WILL NOT SHOW DETAILS FOR ANY MULTIPLE SOLUTIONS"
					   : $"WILL SHOW UP TO {MaxSolutionsToList} {(DisplayPositions ? "BOARD POSITIONS" : "MOVE LISTS (WITHOUT BOARDS)")} FOR MULTIPLE SOLUTIONS.",
				   Success);
		}
		else
			Report("INVALID SOLUTIONS DISPLAY LIMIT (MUST EITHER BE 0 OR GREATER THAN 1)", Error);
	}

	#endregion

	#region Private types, fields, and properties

	#region Types

	private sealed record Position(ChessBoard Board)
	{
		public sealed record MoveSet
		{
			[JsonProperty]
			private byte[] _moves = Empty<byte>();

			[JsonProperty]
			private string _captures = string.Empty;

			[JsonProperty]
			private string _promotions = string.Empty;

			//	Captures and Promotions (below) could be [JsonProperty] auto-properties, but this
			//	means changing "init" to "set", giving them public setters that they don't need.
			public string Captures
			{
				get => _captures;
				init => _captures = value;
			}

			public string Promotions
			{
				get => _promotions;
				init => _promotions = value;
			}

			public string Moves
			{
				get => Decompress(_moves);
				init => _moves = Compress(value);
			}

			public bool HasCaptured(string pieces)
				=> pieces.All(c => Captures.Count(p => p == c) >= pieces.Count(p => p == c));

			public bool HasPromoted(string pieces)
				=> pieces.All(c => Promotions.Count(p => p == c) >= pieces.Count(p => p == c));
		}

		[JsonProperty]
		public List<MoveSet> MoveSets { get; set; } = new () { new () };

		[JsonProperty]
		public bool CheckFuture { get; set; }

		//	We don't need to serialize the ChessBoard for storage;
		//	it has issues doing so, and we can rebuild it from FEN.
		[JsonIgnore]
		public ChessBoard Board { get; private set; } = Board;

		public static readonly Position Impossible = new (new ChessBoard());

		public bool IsPossible
			=> !ReferenceEquals(this, Impossible);

		public void SetBoard(string fen, int turn)
			=> Board = ChessBoard.LoadFromFen($"{fen} 0 {turn}");
	}

	private sealed record StenoData
	{
		public int Index { get; init; }
		public string Marks { get; init; } = string.Empty;
		public string MetaMarks { get; set; } = string.Empty;
		public string Conditions { get; init; } = string.Empty;
		public string MetaConditions { get; set; } = string.Empty;
		public PieceColor Color => Index % 2 is 0 ? PieceColor.White : PieceColor.Black;
	}

	#endregion

	#region Steno marks

	private const string StandardBackRank = "RNBQKBNR";
	private const string ClassicMarks = "12345678abcdefghlnoqrxKLNOPQR%=+~#";
	private const string ExtendedMarks = ClassicMarks + @"B|_\/^pv<>""0-"; // Be sure to keep the - at the end for RegEx help
	private const string PgnMarks = "~.12345678abcdefghx=+#/BNRQKO-"; // ditto
	private const string NonPgnPromotionMarks = "rnlqp";
	private const char EnPassantMark = '%';

	private static readonly string CaptureMarks = $"x{EnPassantMark}";

	private IEnumerable<StenoData> FutureCheckMarks => _stenoData.Select(s => s with { Marks = Concat(s.Marks.Where(c => MarksForFuture.Contains(c))) })
																 .Where(static s => s.Marks.Any())
																 .ToList();

	private byte[] _checkpoint = Empty<byte>();

	private string Marks => _lingo switch
							{
								C => ClassicMarks,
								P => PgnMarks,
								E => ExtendedMarks,
								_ => throw new ("Unrecognized Lingo")
							};

	private string CastlingMarks => _lingo switch
									{
										E => "Oo0",
										C => "Oo",
										_ => "O-"
									};

	private string PromotionMarks => _lingo is PGN ? "=" : NonPgnPromotionMarks;
	private string EndgameMarks => $"#{ForcedDrawMarks}{(_lingo is Extended ? "MS" : null)}";
	private string MarksForFuture => $"{CastlingMarks}{PromotionMarks}P"; // TODO: should have more...EnPassantMark, CaptureMarks
	private string ForcedDrawMarks => $"{(_lingo is PGN ? '/' : '=')}";

	#endregion

	#region Other chess-related data

	private bool StandardSetup => !_startFen.Any();
	private static char QueensRooksFile => (char)(BackRank.IndexOf('R') + 'a');
	private static char KingsRooksFile => (char)(BackRank.LastIndexOf('R') + 'a');
	private static char KingsFile => (char)(BackRank.LastIndexOf('K') + 'a');

	#endregion

	#region Solver settings and data

	private const int MaxSettingsValue = 2_000_000_000;
	private const int ChunkSize = 1_000;
	private const char SavedDataSeparator = default;

	private readonly List<StenoData> _stenoData = new ();
	private readonly Dictionary<string, Position> _newPositions = new ();
	private readonly List<StenoData> _checkpointStenoData = new ();
	private readonly Stopwatch _stopwatch = new ();
	private const string BackRank = StandardBackRank;
	private readonly MessageHandler? _report;
	private readonly bool _showMetaMarks;
	private readonly bool _allowChunking;

	private Dictionary<string, Position> _positions = new ();
	private Dictionary<string, Position> _checkpointPositions = new ();
	private CancellationTokenSource _abortSolveTokenSource = new ();
	private Lingo _lingo;
	private string _steno = string.Empty;
	private string _reportLineFormat = string.Empty;
	private string _outputFile = string.Empty;
	private string _startFen = string.Empty;
	private int _maxPositionsToExamine = 200_000;
	private int _maxSolverTasks = 4;
	private int _totalPositions;
	private int _stenoCountWidth;
	private int _startChunkNumber;
	private int _endChunkNumber;
	private int _positionCount;
	private int _workedPositions;
	private int _maxCooksToKeep;
	private bool _startFromCheckpoint;

	private bool SolveAborted => _abortSolveTokenSource.IsCancellationRequested;

	#endregion

	#endregion

	#region Private methods

	private void Report(string text, MessageType type = Status)
	{
		if (SolveAborted)
			return;
		if (type is MessageType.Abort)
			StopSolving();
		_report?.Invoke(text, type);
		if (type is Status && _outputFile.Any())
			File.AppendAllText(_outputFile, text + '\n');
	}

	private static byte[] Compress(string value)
	{
		var bytes = Encoding.UTF8.GetBytes(value);
		using var memoryStream = new MemoryStream();
		//	This next one cannot be a using statement, unless we do a compressStream.Flush() before the return
		using (var compressStream = new BrotliStream(memoryStream, Optimal))
			compressStream.Write(bytes, 0, bytes.Length);
		return memoryStream.ToArray();
	}

	private static string Decompress(byte[] bytes)
	{
		using var memoryStream = new MemoryStream(bytes);
		using var outputStream = new MemoryStream();
		//	This next one cannot be a using statement, unless we do a compressStream.Flush() before the return
		using (var decompressStream = new BrotliStream(memoryStream, CompressionMode.Decompress))
			decompressStream.CopyTo(outputStream);
		return Encoding.UTF8.GetString(outputStream.ToArray());
	}

	private void StopSolving()
		=> _abortSolveTokenSource.Cancel();

	private bool ValidateSteno()
	{
		//	First remove all comments and whitespace until there aren't any more
		Match match;
		while ((match = Match(_steno, @"(\([^()]*\)|\s)+")).Success)
			_steno = _steno.Replace(match.Value, string.Empty);

		//	Next look for, process, and remove checkpoint chunking.
		if (!SetStartAndEndChunk())
			return false;

		//	Restore to save-point if instructed to do so up front.
		_startFromCheckpoint = _steno.StartsWith('$');
		if (_startFromCheckpoint)
		{
			_steno = _steno[1..];
			_stenoData.AddRange(_checkpointStenoData);
		}

		if (_lingo is not PGN)
			_steno = _steno.Replace("L'", "l")
						   .Replace("N'", "n")
						   .Replace("Q'", "q")
						   .Replace("R'", "r");
		if (!_steno.Any())
			return true;
		if (_steno.Count(static c => c is '$') > 1)
		{
			Report("INVALID STENO: Only one checkpoint mark ($) can appear.", Error);
			return false;
		}
		var regex = $"[{Marks}]([&!][{Marks}])*".Replace("\\", @"\\");
		const string conditionRegex = "([v^][2-7]|" + // there is a pawn advanced to that rank or above/below
									  "[PpRrNnQqBbKkDdLl-][a-h]?[1-8]?|" + // there is a piece of this type (or nothing) on that rank, file, or square
									  "=[RrNnQqBbDdLl]*|" + // a promotion has been made to all pieces listed
									  "x[RrNnQqBbDdLlPp]|" + // this piece was captured on this move
									  "X[RrNnQqBbDdLlPp]|" + // all of these pieces have been captured
									  "@[a-h][1-8]?|@[1-8]" + // the moving piece started on this square/rank/file
									  "+)";
		regex += $@"(\[{conditionRegex}([&|]{conditionRegex})*\])*\$?";
		regex = "^" + regex;
		var stenoChopper = _steno;
		for (var index = _stenoData.Count; stenoChopper.Any() && (match = Match(stenoChopper, regex)).Success; stenoChopper = stenoChopper[match.Length..])
			_stenoData.Add(new ()
						   {
							   Index = index++,
							   Marks = match.Value.TrimEnd('$').Split('[').First(),
							   Conditions = match.Value.Contains('[')
												? match.Value[match.Value.IndexOf('[')..]
												: match.Value[^1] is '$'
													? "$"
													: string.Empty
						   });
		if (stenoChopper.Any())
		{
			Report($"INVALID STENO: The problem is found here: {stenoChopper}", Error);
			return false;
		}

		var moveMarks = _stenoData.Select(static s => s.Marks).ToList();
		var whiteMoves = _stenoData.Where(static s => (s.Index & 1) is 0).Select(static s => s.Marks).ToList();
		var blackMoves = _stenoData.Where(static s => (s.Index & 1) is 1).Select(static s => s.Marks).ToList();
		var error = string.Empty;
		var enPassant = $"{EnPassantMark}";
		var totalMoves = moveMarks.Count;
		if (moveMarks.CountMarksContaining(EndgameMarks, totalMoves - 1) is not 0)
			error = "An endgame mark (checkmate or draw) cannot appear before the final steno mark.";
		else if (whiteMoves.CountMarksContaining(CastlingMarks) > 1 || blackMoves.CountMarksContaining(CastlingMarks) > 1)
			error = "A player can only castle once.";
		else if (whiteMoves.CountMarksContaining(PromotionMarks) > 8 || blackMoves.CountMarksContaining(PromotionMarks) > 8)
			error = "A player cannot make more than 8 promotions.";
		else if (whiteMoves.CountMarksContaining(enPassant) > 8 || blackMoves.CountMarksContaining(enPassant) > 8)
			error = "A player cannot make more than 8 en passant captures.";
		else if (whiteMoves.CountMarksContaining(CaptureMarks) > 15 || blackMoves.CountMarksContaining(CaptureMarks) > 15)
			error = "A player cannot make more than 15 captures.";
		else if (StartFen.Length is 0 or 8)
			// 0-char StartFen is standard start; 8-char StartFens are Fischer Random games starting from move 1
			if (moveMarks.CountMarksContaining(enPassant, 4) > 0)
				error = "A player cannot make an en passant capture before his third turn.";
			else if (moveMarks.CountMarksContaining(PromotionMarks, 8) > 0)
				error = "A player cannot promote a pawn before his fifth turn.";
			else if (moveMarks.CountMarksContaining("x+#", 2) > 0)
				error = "A player cannot make a capture or a deliver a check on his first turn.";
			else if (moveMarks.CountMarksContaining(@"_/\""", 2) > 0
				  || whiteMoves[0].Contains('v') || blackMoves.Take(1).Any(static x => x.Contains('^')))
				error = "A player cannot make a move fitting any of the marks ^ (or v), _, /, \\, or \"  on his first turn.";
			else if (StartFen.Length is 0)
				// Conditions stop being checked for Fischer game-start setups here
				if (moveMarks.CountMarksContaining("#", 3) > 0)
					//	In Fischer, Mate on White's second move seems possible only (?) in N-[BN]-Q-[BN]-B-R-K-R (or that, reversed)
					error = "Checkmate cannot be delivered before Black's second turn.";
				else if (moveMarks.CountMarksContaining("o", 6) > 0)
					error = "A player cannot castle king-side before his fourth turn.";
				else if (moveMarks.CountMarksContaining("O", _lingo is PGN ? 6 : 8) > 0)
					error = "A player cannot castle so early in the game.";
				else if (moveMarks.CountMarksContaining($"{ForcedDrawMarks}", 18) > 0)
					error = "A forced draw before Black's ninth move is impossible.";
		if (error.Any())
		{
			Report($"INVALID STENO: {error}\n", Error);
			return false;
		}
		AddMetaConditions();
		return true;

		void AddMetaConditions()
			=> _stenoData.ForEach(each =>
								  {
									  //	Forbid any game-ending move before the final mark of the steno.
									  //	I really truly double-checked; this actually does speed things up.
									  if (StandardSetup && each.Index > 2 && each.Index < _stenoData.Count - 1)
										  each.MetaMarks += $"!#{(each.Index > 17 ? $"!{ForcedDrawMarks}" : null)}";

									  var isWhite = each.Color == PieceColor.White;

									  var positiveMarks = Replace(each.Marks, @"!\.", string.Empty);

									  //	Promotions mean either that a pawn must have advanced at least to a specific
									  //	rank on prior turns or the piece type must have promoted earlier.
									  if (each.Marks.Any(PromotionMarks.Contains))
									  {
										  var promotedPieceType = new string(positiveMarks.Where("NnLlRrBbQq".Contains).ToArray()).ToUpper().Replace('L', 'B');
										  AddPawnAdvances(each.Index, isWhite, promotedPieceType.Length is 1
																				   ? isWhite ? promotedPieceType[0] : char.ToLower(promotedPieceType[0])
																				   : default);
									  }

									  //	Castling means that the king (and rook, if known) never moved, and that one HALF AND FULL turn before the castle,
									  //	the squares are empty.	For non-Standard setup games, we only add the "squares are empty" condition for the
									  //	half-move before the castle.  And notice that on a Standard QueenSide castle, it may be the case that an opponent's
									  //	bishop or knight could be on the b-square a full turn before castling.
									  if (!positiveMarks.Any(CastlingMarks.Contains))
										  return;
									  var rank = isWhite ? 1 : 8;
									  var rook = isWhite ? 'R' : 'r';
									  //	We know it's a Kings-side castle if the mark is 'o' or if it is the
									  //	fourth move for the player and the mark is a castle to either side.
									  var kingSide = positiveMarks.Any((_lingo switch
																		{
																			E when each.Index < 8 => "0o",
																			P when each.Index < 8 => "O-",
																			_                     => "o"
																		}).Contains)
												  && StandardSetup;
									  var queenSide = _lingo is not PGN && positiveMarks.Contains('O');
									  //	Since the King and rook cannot move on the first turn, we can .Skip(2) here.
									  foreach (var priorMove in _stenoData.Take(each.Index).Skip(2))
									  {
										  if (priorMove.Color == each.Color)
											  priorMove.MetaMarks += "!K";
										  if (kingSide || queenSide)
											  priorMove.MetaConditions += $"[{rook}{(kingSide ? KingsRooksFile : QueensRooksFile)}{rank}]";
									  }
									  //	The king may not castle out of check
									  _stenoData[each.Index - 1].MetaMarks += "!+";
									  //	Mandate empty squares between the castling pieces
									  if (kingSide)
										  if (StandardSetup || KingsFile is 'e' && KingsRooksFile is 'h')
										  {
											  //	Yes, we could share the "else" code, but since we know what we know, this speeds us up a (very) little.
											  var emptySquares = $"[-g{rank}&-f{rank}]";
											  _stenoData[each.Index - 1].MetaConditions += emptySquares;
											  //	...and since this is unique to StandardSetup (for now), we'd be checking the "if" anyway.
											  _stenoData[each.Index - 2].MetaConditions += emptySquares;
										  }
										  else
										  {
											  var emptySquares = string.Empty;
											  for (var file = KingsRooksFile - 1; file > KingsFile; emptySquares += (char)file--) { }
											  if (emptySquares.Any())
												  _stenoData[each.Index - 1].MetaConditions += $"[{Join('&', emptySquares.Select(c => $"-{c}{rank}"))}]";
										  }
									  else if (queenSide)
										  if (StandardSetup || KingsFile is 'e' && QueensRooksFile is 'a')
										  {
											  _stenoData[each.Index - 1].MetaConditions += $"[-d{rank}&-c{rank}&-b{rank}]";
											  var bishop = isWhite ? 'b' : 'B';
											  var knight = isWhite ? 'n' : 'N';
											  _stenoData[each.Index - 2].MetaConditions += $"[-d{rank}&-c{rank}][-b{rank}|{bishop}b{rank}|{knight}b{rank}]";
										  }
										  else
										  {
											  var emptySquares = string.Empty;
											  for (var file = QueensRooksFile + 1; file < KingsFile; emptySquares += (char)file++) { }
											  if (emptySquares.Any())
												  _stenoData[each.Index - 1].MetaConditions += $"[{Join('&', emptySquares.Select(c => $"-{c}{rank}"))}]";
										  }
								  });
	}

	private bool SetStartAndEndChunk()
	{
		if (_steno.Count(static c => c is '*') is not 1)
		{
			_startChunkNumber = _endChunkNumber = default;
			return true;
		}
		if (!_allowChunking)
		{
			Report("INVALID STENO: CHECKPOINT CHUNKING IS DISABLED.", Error);
			return false;
		}
		if (!_checkpointPositions.Any())
		{
			Report("INVALID STENO: No existing checkpoint to be chunked!", Error);
			return false;
		}
		var parts = _steno.Split('*');
		_steno = $"${parts[1]}";
		try
		{
			if (parts[0].Count(static c => c is '-') is 1)
			{
				var numbers = parts[0].Split('-').Select(static s => s.Trim()).ToArray();
				_startChunkNumber = Parse(numbers[0]);
				_endChunkNumber = numbers[1].Any() ? Parse(numbers[1]) : MaxValue;
			}
			else
				_endChunkNumber = _startChunkNumber = Parse(parts[0]);
			var lastChunkNumber = _checkpointPositions.Count / ChunkSize + 1;
			_endChunkNumber = Math.Min(_endChunkNumber, lastChunkNumber);
			if (_startChunkNumber < 1 || _endChunkNumber < _startChunkNumber)
				throw new ();
			if (_startChunkNumber == _endChunkNumber || !parts[1].Contains('$'))
				return true;
			Report("INVALID STENO: Checkpoint ($) cannot be set during multi-chunk work.", Error);
		}
		catch
		{
			Report($"INVALID CHECKPOINT CHUNK: {parts[0]}*");
		}
		return false;
	}

	private void AddPawnAdvances(int index, bool isWhite, char piece)
	{
		for (var turn = 1; turn < 5; ++turn)
		{
			var turnIndex = index - turn * 2;
			var orPromotion = turnIndex < 10 ? null : piece == default ? isWhite ? "|=Q|=R|=B|=N" : "|=q|=r|=b|=n" : $"|={piece}";
			var condition = $"[{(isWhite ? '^' : 'v')}{(isWhite ? 8 - turn : turn + 1)}{orPromotion}]";
			_stenoData[turnIndex].MetaConditions += condition;
			//	Put the same mark on the succeeding turn by the opponent too, to make sure he doesn't capture the pawn.
			_stenoData[++turnIndex].MetaConditions += condition;
		}
	}

	private void RunSolve()
	{
		var startPosition = ChessBoard.LoadFromFen(StartFen) ?? throw new ($"Could not create ChessBoard from {StartFen}");
		_stenoCountWidth = $"{_stenoData.Count}".Length;
		_reportLineFormat = $"{{0,{_stenoCountWidth}}}/{_stenoData.Count} {{1,10:N0}} {{2}}\t{{3}}\t{{4}}";
		var tasks = new Task[_maxSolverTasks];

		if (_startFromCheckpoint)
			AddFutureConditions(_checkpointStenoData.Last());

		for (var chunkNumber = _startChunkNumber; chunkNumber <= _endChunkNumber; ++chunkNumber)
		{
			_positions = _startFromCheckpoint && _checkpointPositions.Any()
							 ? _checkpointPositions
							 : new () { [startPosition.FenWithoutMoveCounts()] = new (startPosition) };

			if (chunkNumber > 0)
			{
				var start = ChunkSize * (chunkNumber - 1);
				var stop = Math.Min(start + ChunkSize, _positions.Count);
				Report($"\n-- {_positions.Count:N0} POSITIONS; WORKING CHUNK FROM #{start + 1:N0} TO #{stop:N0} --");
				_positions = _positions.OrderBy(static pair => pair.Key).Skip(start).Take(ChunkSize)
									   .ToDictionary(static pair => pair.Key, static pair => pair.Value);
			}

			//	Start the solve....
			//	Start with an empty "newPositions" Dictionary that will contain any and all positions that result from making each
			//	mark-matching move that can be made on each of the "workload" worth of possible boards that we will go through.
			_totalPositions = 0;
			_newPositions.Clear();
			if (_startFromCheckpoint)
				Report($"{new (' ', _stenoCountWidth * 2 + 11)}$ {SavedSteno}");
			_stopwatch.Restart();
			foreach (var each in _stenoData.Skip(_startFromCheckpoint ? _checkpointStenoData.Count : 0))
			{
				_positionCount = _positions.Count;
				_totalPositions += _positionCount;
				Report(Format(_reportLineFormat, each.Index, _positionCount, each.Marks, each.Conditions,
							  _showMetaMarks ? each.MetaMarks + each.MetaConditions : null));

				//	Run a number of concurrent tasks to each work a chunk of the possible positions to see if the current steno mark
				//	mark can be played on those positions, and if so, fill the newPositions Dictionary with the resulting positions.
				var chunkSize = _positionCount / _maxSolverTasks;
				var boardList = _positions.Values;
				_workedPositions = 0;
				for (var taskNumber = 0; taskNumber < _maxSolverTasks; ++taskNumber)
				{
					var chunk = boardList.Skip(taskNumber * chunkSize);
					if (taskNumber + 1 < _maxSolverTasks)
						chunk = chunk.Take(chunkSize);
					var workload = chunk.ToList();
					tasks[taskNumber] = workload.Any()
											? Task.Run(() => MakeMoveInPositions(workload, each), _abortSolveTokenSource.Token)
											: Task.CompletedTask;
				}
				Task.WaitAll(tasks);

				//	Copy the new positions (if any) to the "positions" list.
				_positions = _newPositions.Where(static p => p.Value.IsPossible)
										  .ToDictionary(static pair => pair.Key, static pair => pair.Value);
				//	If there were no newPositions, we are out of here.  Otherwise, clear the "newPositions"
				//	Dictionary and go on to the next mark, which will work the "positions" Dictionary again.
				if (!_positions.Any())
					break;
				_newPositions.Clear();

				if (each.Conditions.EndsWith('$'))
					EstablishCheckpoint(each.Index);

				AddFutureConditions(each);
			}
			_stopwatch.Stop();

			//	Finish up (this chunk)
			DisplayResults();
		}
	}

	private void MakeMoveInPositions(IEnumerable<Position> workload, StenoData each)
	{
		var index = each.Index;
		var marks = each.Marks + each.MetaMarks;
		var conditions = each.MetaConditions + each.Conditions;
		ChessBoard newBoard;
		string newFen;
		bool checkFuture;
		List<Position.MoveSet> moveSets;
		var thousandthsOfPositionCount = Math.Max(_positionCount / 1000, 1);

		foreach (var position in workload.TakeWhile(_ => !SolveAborted))
		{
			if (++_workedPositions % thousandthsOfPositionCount is 0)
				ReportProgress(index);
			foreach (var move in position.Board.Moves().TakeWhile(_ => !SolveAborted))
			{
				//	This call does the meat of the work.
				if (!MoveMatchesMarks(position, move, out var mustDraw))
					continue;

				moveSets = position.MoveSets;

				//	If we get here, the move fits the mark.  Make a new board depicting the position after the move is made.
				var potentialNewBoard = position.Board.MakeMove(move);
				if (potentialNewBoard is null)
					continue;
				newBoard = potentialNewBoard;

				//	Get the FEN for the new position and see if it's already been worked.
				//	It does NOT save time to do this before checking if MoveMatchesMarks()!
				newFen = newBoard.FenWithoutMoveCounts();
				if (_newPositions.ContainsKey(newFen))
					checkFuture = false;
				//	Check for a forced draw (not realized until after the move has been made on the board).
				else if (mustDraw.HasValue && newBoard.EndGame is { EndgameType: EndgameType.Stalemate or EndgameType.InsufficientMaterial } != mustDraw)
					continue;
				//	Check any conditions that the setter has required at this point.
				else if (!CheckConditions(move))
					continue;
				//	If there are marks yet to come that can be used to eliminate this board from being
				//	possible (such as a castling mark when castling is no longer possible for the player
				//	after the move being made now), check for those.  Since multiple move-sets can reach
				//	the same FEN, we make sure that this FEN has not yet been future-checked before we
				//	go ahead and do it.
				else if ((checkFuture = position.CheckFuture) && !FenCouldSolve())
				{
					lock (_newPositions)
						_newPositions[newFen] = Position.Impossible;
					continue;
				}

				//	TODO: Check that this isn't the third time we've seen this position during this game; if so, draw.

				//	We will accept this position as something that we need to check any remaining marks on.
				//	It may have the same FEN (different move-set or move-order) to other boards we are
				//	accepting, so keep those all together as one Dictionary entry keyed on the FEN, and
				//	record all the different MoveSets that get us there.
				var result = newBoard.EndGame is { EndgameType: EndgameType.Stalemate or EndgameType.InsufficientMaterial }
								 ? "½-½"
								 : newBoard.IsEndGame
									 ? newBoard.ToPgn().Split().Last()
									 : null;
				moveSets = moveSets.Select(m => m with { Moves = $"{m.Moves}{move.San?.TrimEnd('$')} {result}" }).ToList();
				AddNewPosition();
			}
		}

		bool MoveMatchesMarks(Position position, Move move, out bool? mustDraw)
		{
			var fromSquare = $"{move.OriginalPosition}";
			var toSquare = $"{move.NewPosition}";
			var piece = $"{move.Piece.Type.AsChar}".ToUpper().Single();
			var kingSide = move.IsCastling(CastleType.King);
			var queenSide = move.IsCastling(CastleType.Queen);
			var pgnCastling = _lingo is PGN && move.IsCastling();

			//	Draw for insufficient material needs (double-)checking after the move has been made,
			//	so set a flag to tell the caller to verify the condition after making the move.
			mustDraw = null;

			//	Set a flag saying that the move must match (as opposed to must NOT match) the mark
			var mustHoldTrue = true;
			foreach (var mark in marks.Where(static m => m is not '&'))
			{
				//	See if the move and the mark are a match for each other
				switch (mark)
				{
				case '!':
					//	The next mark must NOT be true; set the flag saying so.
					mustHoldTrue = false;
					continue;
				//	Any valid move
				case '~' or '.':
				//	Check and mate
				case '+' when move.IsCheck && !move.IsPromotion():
				case '#' when move is { IsCheck: true, IsMate: true } && !move.IsPromotion():
				//	Stalemate
				case var _ when ForcedDrawMarks.Contains(mark) && move is { IsCheck: false, IsMate: true }:
				//	Promotion to any piece
				case 'p' when _lingo is E && move.IsPromotion():
				case '=' when _lingo is P && move.IsPromotion():
				//	Ranks and files; for castling, the toSquare will only give us the King's toSquare, but we know where the rook moved
				//	Notice, though, that PGN castling MUST be represented ONLY by O; the piece and destination square aren't in the PGN
				case >= '1' and <= '8' when !pgnCastling && toSquare.Contains(mark):
				case >= 'a' and <= 'h' when !pgnCastling && (toSquare.Contains(mark) || kingSide && mark is 'g' || queenSide && mark is 'c'):
				//	In a promotion, move.San will be null here, so to detect things like the "g" in "gxf8=N", we must check...
				case >= 'a' and <= 'h' when _lingo is P && move.IsCapture() && move.IsPromotion() && $"{move.OriginalPosition}"[0] == mark:
				//	Piece types, and promotions to those types
				case 'P' or 'R' or 'N' or 'B' or 'Q' when piece == mark:
				case 'K' when !pgnCastling && piece == mark:
				//	Apparently, castling is considered to be ONLY a king move.
				//	case 'R' when _lingo is not PGN && move.IsCastling():
				case 'L' when piece is 'B':
				case 'r' or 'l' or 'n' or 'q' when move.IsPromotion(mark):
				//	Captures
				case 'x' when move.CapturedPiece is not null:
				//	En passant
				case EnPassantMark when move.Parameter is MoveEnPassant:
				//	Castling
				case 'o' when kingSide:
				case 'O' when queenSide:
				case '0' when _lingo is Extended && (kingSide || queenSide):
				//	The marks below are the differences between PGN and Classic/Extended
				case 'O' when _lingo is PGN && kingSide:
				case '-' when _lingo is PGN && (kingSide || queenSide):
				case >= 'a' and <= 'h' or >= '1' and <= '8' when _lingo is PGN && move.IsDisambiguatedBy(mark):
				case >= 'a' and <= 'h' when _lingo is PGN && move.San?.StartsWith($"{mark}x") is true:
				case 'P' or 'R' or 'N' or 'B' or 'Q' or 'K' when _lingo is PGN && move.IsPromotion(mark):
				//	The marks below only exist in Extended
				//	Non-capturing move
				case '-' when _lingo is Extended && move.CapturedPiece is null:
				//	Move in a specific direction
				case '_' or '|' when fromSquare[mark is '_' ? 1 : 0] == toSquare[mark is '_' ? 1 : 0]:
				case '/' or '\\' when _lingo is not PGN && move.IsDiagonal(mark):
				case '<' or '>' when move.IsToSide(mark):
				case '^' or 'v' when move.IsToBase(mark):
				case '"' when (moveSets = MovingPieceLastMoved()).Any():
					//	The move is a valid match for this mark on this board...
					if (!mustHoldTrue)
						//	...and that's bad.
						return false;
					//	No, actually, it's good.  Go on to the next mark.
					break;
				//	Non-stalemate draw (checked after the move is made) -- this case must be AFTER the case for stalemate
				case var _ when ForcedDrawMarks.Contains(mark) && mustDraw is not false:
					mustDraw = mustHoldTrue;
					break;
				default:
					//	The move is NOT a valid match for this mark on this board...
					if (mustHoldTrue)
						//	...and that's bad.
						return false;
					//	No, actually, it's good.  Go on to the next mark.
					break;
				}
				//	Unless we're told otherwise, the next mark (if any) is not negated by an exclamation point.
				mustHoldTrue = true;
			}
			return true;

			List<Position.MoveSet> MovingPieceLastMoved()
				=> position.MoveSets
						   .Where(s =>
								  {
									  var priorMoves = s.Moves.TrimEnd().Split();
									  if (priorMoves.Length < 2)
										  return false;
									  var san = priorMoves[^2].TrimEnd('+').Split('=').First().Split('.').Last();
									  var backRankNumber = move.Piece.Color == PieceColor.White ? 1 : 8;
									  return (san switch
											  {
												  "O-O-O" => $"{KingsFile}{backRankNumber}c{backRankNumber}",
												  "O-O"   => $"{KingsFile}{backRankNumber}g{backRankNumber}",
												  _       => san[^2..]
											  }).Contains($"{move.OriginalPosition}");
								  })
						   .ToList();
		}

		bool CheckConditions(Move move)
		{
			if (move.IsPromotion())
				moveSets = moveSets.Select(m => m with { Promotions = m.Promotions + move.PiecePromoted() }).ToList();
			if (move.IsCapture())
				moveSets = moveSets.Select(m => m with { Captures = m.Captures + move.PieceCaptured() }).ToList();

			var conditionsWithoutSave = conditions.TrimEnd('$');
			if (!conditionsWithoutSave.Any())
				return true;

			foreach (var multiCondition in conditionsWithoutSave[1..^1].Split("]["))
			{
				var conditionsMet = true;
				foreach (var orCondition in multiCondition.Split('|'))
				{
					conditionsMet = orCondition.Split('&')
											   .All(condition => condition[0] switch
																 {
																	 'x'                            => move.PieceCaptured().Contains(condition[1]),
																	 'X'                            => moveSets.All(m => m.HasCaptured(condition[1..])),
																	 '=' when condition.Length is 0 => moveSets.All(static m => m.Promotions.Any()),
																	 '='                            => moveSets.All(m => m.HasPromoted(condition[1..])),
																	 '^'                            => newBoard.PawnAdvancedTo(PieceColor.White, condition[1]),
																	 'v'                            => newBoard.PawnAdvancedTo(PieceColor.Black, condition[1]),
																	 '-'                            => newBoard.SquareIsEmpty(condition[1..]),
																	 _                              => newBoard.HasPieceOnSquare(condition[0], condition[1..])
																	 //	TODO: Add O, o, and 0 (must have castled)?
																 });
					if (conditionsMet)
						break;
				}
				if (!conditionsMet)
					return false;
			}
			return true;
		}

		void AddNewPosition()
		{
			lock (_newPositions)
			{
				if (SolveAborted)
					return;
				if (_newPositions.TryGetValue(newFen, out var position))
				{
					if (position.MoveSets.Count < _maxCooksToKeep + 1)
						position.MoveSets.AddRange(moveSets.Take(_maxCooksToKeep - position.MoveSets.Count + 1));
				}
				else if (_newPositions.Count < _maxPositionsToExamine)
				{
					_newPositions[newFen] = new (newBoard) { CheckFuture = checkFuture, MoveSets = moveSets.ToList() };
					if (_newPositions.Count % 1000 is 0)
						ReportProgress(index);
				}
				else
				{
					_newPositions.Clear();
					Report($"\nGAVE UP; REACHED THE POSITION LIMIT OF {_maxPositionsToExamine:N0}.", MessageType.Abort);
				}
			}
		}

		bool FenCouldSolve()
		{
			//	Get a list of all marks yet to come in the Steno string that might be used to rule this board's position out.
			var marksToBeChecked = FutureCheckMarks.Where(s => s.Index > index).ToList();

			//	If there were no such marks, set the "checkFuture" flag to false (so we don't bother checking this again)
			//	and declare the position good to go from a future-checking standpoint (as much as is coded yet, at least).
			if (!(checkFuture = marksToBeChecked.Any()))
				return true;

			//	Go through all the upcoming marks that might tell us the current position can't lead to a solution.
			foreach (var upcoming in marksToBeChecked)
				foreach (var mark in upcoming.Marks)
					switch (mark)
					{
					case '0':
					case 'O' when _lingo is PGN:
						//	Check that castling to either side is still available for this color.
						if (!newFen.Split()[2].Any(c => upcoming.Color == PieceColor.Black ? char.IsLower(c) : char.IsUpper(c)))
							return false;
						break;
					case 'o' or 'O':
						//	Check that castling to a specified side is still available for this color.
						var symbol = mark is 'o' ? "K" : "Q";
						if (upcoming.Color == PieceColor.Black)
							symbol = symbol.ToLower();
						if (!newFen.Split()[2].Contains(symbol.Single()))
							return false;
						break;
					case 'B' or 'R' or 'N' or 'Q' or 'L':
						//	TODO: if there is no such piece for this color on the current position, be sure a promotion can happen...
						break;
					case var _ when mark == EnPassantMark || PromotionMarks.Contains(mark):
					case 'P':
						//	TODO: this only checks that there is at least a single pawn left in this position
						//	We could do better:
						//		check that there are ENOUGH pawns to account for each of the promotions
						//		check that there is an OPPOSITION pawn still in its starting rank for each upcoming en passant
						//		check that there are enough pawns still not advanced too much for each upcoming en passant
						if (!newFen.Contains(upcoming.Color == PieceColor.Black ? 'p' : 'P'))
							return false;
						break;
					case '#':
						//	TODO: check that there is sufficient mating material for the color
						break;
					}
			return true;
		}
	}

	private void ReportProgress(int index)
		=> Report(Format(_reportLineFormat, index + 1, _newPositions.Count, $"@ {_workedPositions * 100 / (decimal)_positionCount:N1}%", null, null),
				  InProgress);

	private void EstablishCheckpoint(int index)
	{
		_checkpointStenoData.Clear();
		_checkpointStenoData.AddRange(_stenoData.Take(index + 1).Select(static s => s with { Conditions = s.Conditions.TrimEnd('$') }));
		_checkpointPositions.Clear();
		_positions.ToList().ForEach(pair => _checkpointPositions.Add(pair.Key, pair.Value));
	}

	private void AddFutureConditions(StenoData data)
	{
		//	TODO: THIS IS GREAT, BUT IT MIGHT BE SLOWING US DOWN SOME? MAYBE FIND A WAY TO DO IT ONLY IF A CAPTURE HAPPENED ON ONE OF THE POSITIONS?
		//	If the queen (or both rooks, etc.) is gone in all positions, but we see one in the future, add a meta-condition [=Q] where it is seen.
		//	The pieces that could have gone extinct are those of the OTHER color from the one that just moved.
		var potentialGoners = data.Color == PieceColor.White ? "b" + "n" + "q" + "r" : "B" + "N" + "Q" + "R";
		//	See if any of those pieces are indeed extinct, because they don't show up in the FEN of any possible position.
		foreach (var piece in potentialGoners.Where(piece => _positions.Values.All(p => !p.Board.Contains(piece))))
			//	Go through the upcoming steno-marks for the player with the extinct piece-type
			for (var future = data.Index + 1; future < _stenoData.Count; future += 2)
				if (_stenoData[future].Marks.Replace('L', 'B').Contains(char.ToUpper(piece)))
				{
					var regenerationCondition = $"[={piece}]";
					var regenerationIndex = future - (_lingo is PGN ? 0 : 2);
					//	Maybe we already learned that this piece-type went extinct, and already said so; if so, check the next piece type.
					if (_stenoData[regenerationIndex].MetaConditions.Contains(regenerationCondition))
						break;
					//	This is the first time we've found that a particular piece-type has gone extinct but is needed later; require the promotion.
					_stenoData[regenerationIndex].MetaConditions += regenerationCondition;
					AddPawnAdvances(regenerationIndex, data.Color == PieceColor.Black, piece);
					//	No other piece type could have gone extinct on this turn, so no need to check them.  Get the solver back to work.
					return;
				}
	}

	private void DisplayResults()
	{
		var totalMoveSets = _positions.Values.Select(static b => b.MoveSets.Count).Sum();
		var atLeast = _positions.Any() && _positions.Values.Max(static b => b.MoveSets.Count) > _maxCooksToKeep ? "AT LEAST " : null;
		Report(Format(_reportLineFormat, _stenoData.Count, _positions.Count,
					  $"POSITION{(_positions.Count is 1 ? null : "S")} " +
					  $"AND {atLeast}{totalMoveSets:N0} GAME{(totalMoveSets is 1 ? null : "S")} " +
					  $"({_totalPositions:N0} POSITION{(_totalPositions is 1 ? null : "S")} EXAMINED IN {_stopwatch.Elapsed.TotalSeconds:N2} SECS)",
					  null, null));
		var positionsToShow = _positions.Count is 1 ? 1 : MaxSolutionsToList;
		if (positionsToShow > 1 && positionsToShow < _positions.Count)
			Report($"\n-- SHOWING THE FIRST {positionsToShow} POSITIONS (WITH UP TO {_maxCooksToKeep:N0} COOK{(_maxCooksToKeep is 1 ? null : 'S')} FOR EACH) --");
		var positionNumber = 0;
		foreach (var (fen, position) in _positions.Take(positionsToShow))
		{
			var gameCount = position.MoveSets.Count;
			if (positionsToShow is 1 || DisplayPositions)
				Report($"\n{(_positions.Count is 1 ? null : $"POSITION {++positionNumber} ")}" +
					   $"{(gameCount is 1 ? null : $" ({gameCount} MOVE SETS) ")}FEN: {fen}\n" +
					   position.Board.ToAscii().Replace(".", "·"));
			Report(Join('\n', position.MoveSets
									  .Select(static m => Join(' ', m.Moves
																	 .TrimEnd()
																	 .Split()
																	 .Select(static (m, i) => $"{(i % 2 is 0
																							   && !m.Contains('½')
																							   && m is not "1-0" and not "0-1"
																									  ? $"{i / 2 + 1}."
																									  : null)}{m}")))));
		}

		if (!_checkpointPositions.Any())
			return;
		//	Create a Checkpoint string that can be held offline and restored to a new Solver();
		var serializedPositions = SerializeObject(_checkpointPositions) ?? throw new ();
		var serializedStenoData = SerializeObject(_checkpointStenoData) ?? throw new ();
		_checkpoint = Compress($"{serializedPositions}{SavedDataSeparator}{serializedStenoData}");
	}

	private static bool ValidateFen(string fen)
		//	TODO
		=> true;

	public void SaveCheckpoint(string filename)
	{
		try
		{
			File.WriteAllBytes(filename, Checkpoint);
		}
		catch (Exception ex)
		{
			Report($"UNABLE TO SAVE CHECKPOINT TO {filename} ({ex.Message})");
		}
	}

	public void LoadCheckpoint(string filename)
	{
		try
		{
			Report("LOADING ...");
			Checkpoint = File.ReadAllBytes(filename);
			Report($"LOADED {SavedSteno} $ ({_checkpointPositions.Count:N0} POSITION{(_checkpointPositions.Count is 1 ? null : 'S')})", Success);
		}
		catch (Exception ex)
		{
			Report($"UNABLE TO LOAD CHECKPOINT FROM {filename} ({ex.Message})");
		}
	}

	#endregion
}
