using steno;
using static System.Console;
using static System.Environment;
using static System.Int32;
using static System.String;
using static steno.Solver;

var solver = new Solver(static (text, type) =>
						{
							if (type is MessageType.InProgress)
								Write($"\r{text}");
							else
								WriteLine($"\r{new string(' ', WindowWidth - 1)}\r{text}");
						});

if (!args.Any())
{
	WriteLine($"\nStenoSolver v{VersionNumber} by Manus Hand." +
			  "\nType ? for help.  Use Ctrl-C to exit the solver.");
	CancelKeyPress += delegate(object? _, ConsoleCancelEventArgs eventArgs)
					  {
						  if (eventArgs.SpecialKey is ConsoleSpecialKey.ControlC)
							  return;
						  // Ctrl-Break
						  eventArgs.Cancel = true;
						  solver.Abort();
					  };

	ReportStatus();
	while (true)
	{
		Write("\nSTENO: ");
		var steno = ReadLine()?.Trim();
		if (IsNullOrEmpty(steno))
			continue;
		var words = steno.Split();
		var command = words[0].ToUpper();
		int number;
		switch (command)
		{
		case "LIMIT" when words.Length is 2:
			solver.MaxPositionsToExamine = words[1];
			break;
		case "SHOW" when words.Length is 2 or 3 && TryParse(words[1], out number):
			if (words.Length is 3 && words[2] is not ("BOARD" or "BOARDS" or "MOVE" or "MOVES"))
				goto badShow;
			var display = words.Length is 3
							  ? words[2].StartsWith('B')
							  : solver.DisplayPositions;
			solver.SetMaxSolutions(number, display);
			break;
		case "SHOW" when words.Length is 2 && words[1] is "BOARD" or "BOARDS" or "MOVE" or "MOVES":
			solver.SetMaxSolutions(solver.MaxSolutionsToList, words[1].StartsWith('B'));
			break;
		case "SHOW":
		badShow:
			WriteLine("INVALID SHOW COMMAND; TYPE ? FOR HELP");
			break;
		case "TASK" or "TASKS" when words.Length is 2 && TryParse(words[1], out number):
			solver.MaxSolverTasks = number;
			break;
		case "COOK" or "COOKS" when words.Length is 2 && TryParse(words[1].ToUpper().Replace("MAX", "2000000000"), out number) && number > 0:
			solver.MaxCooksToKeep = number;
			break;
		case "LINGO" or "MODE" or "VOCABULARY" or "VOCAB" or "STYLE" or "STENO" when words.Length is 2:
			solver.Vocabulary = words[1];
			break;
		case "FILE" or "ECHO":
			//	No filename will reset it by making it string.Empty;
			solver.OutputFile = words.Length is 1 ? Empty : Join(' ', words[1..]);
			break;
		case "SAVE" when words.Length > 1:
			solver.SaveCheckpoint(FileName(words[1..]));
			break;
		case "LOAD" when words.Length > 1:
			solver.LoadCheckpoint(FileName(words[1..]));
			break;
		case "RESET" or "STANDARD":
		case "FISCHER" when words.Length is 2 && words[1].Length is 8:
		case "SETUP" or "START" when words.Length is > 3 and < 8:
			solver.StartFen = words.Length is 1 ? Empty : Join(' ', words[1..]);
			break;
		case "STATUS":
			ReportStatus();
			break;
		case var _ when steno.First() is '?':
			ShowHelpText();
			break;
		case "QUIT":
		case "EXIT":
			Exit(0);
			break;
		case "LIMIT" or "SHOW" or "TASK" or "TASKS"
			 or "COOK" or "COOKS" or "SAVE" or "LOAD" or "SETUP" or "FISCHER"
			 or "LINGO" or "MODE" or "VOCABULARY" or "VOCAB" or "STYLE" or "STENO":
			WriteLine("INVALID SOLVER COMMAND FORMAT; TYPE ? FOR HELP");
			break;
		default:
			solver.Solve(steno);
			break;
		}

		//	Silly little utility function to remove double-quotes from the front and back of a filename string, if given.
		static string FileName(string[] words)
		{
			var filename = Join(' ', words);
			return $"{filename[0]}{filename[^1]}" is """
													 ""
													 """
					   ? filename[1..^1]
					   : filename;
		}
	}

	static void ShowHelpText()
	{
		const string files = "a" + "b" + "c" + "d" + "e" + "f" + "g" + "h";
		const string classicPieces = "P" + "N" + "L" + "R" + "Q" + "K";
		const string classicPromotions = "n" + "l" + "r" + "q";
		const string pgnPieces = "N" + "B" + "R" + "Q" + "K";
		foreach (var text in new[]
							 {
								 "\nWhen entering a Steno, you may use the marks listed below.",
								 "Initially, the solver is using the EXTENDED vocabulary.",
								 "Type MODE then Classic, Extended, or PGN (C, E, or P) to change it.",
								 $"CLASSIC:   {files} 12345678 {classicPieces} +#= {classicPromotions} Oo x% ~",
								 "           N' (etc.) are alternatives for n, l, r, and q",
								 "EXTENDED:  [all classic mode marks]",
								 "           |_/\\      move in the indicated direction",
								 "           <>^v      same; always from White's point of view",
								 "           \"         move the piece on the square last moved-to",
								 "           -         any move that is not a capture",
								 "           0         castling (in either direction)",
								 "           p         promotion to an unknown piece type",
								 "           B         is an alternative for L",
								 $"PGN:       {files}  also recognized when disambiguating (Nac3)",
								 "                     and in pawn captures (axb6)",
								 "           12345678  also when disambiguating, as above (R2c7)",
								 $"           {pgnPieces}     also recognized in promotion (gxf1=R)",
								 "           + and #   check and checkmate (as in Classic)",
								 "           /         forced draw (includes stalemate; instead of =)",
								 "           =         promotion to an unspecified piece type",
								 "           O and -   castling (in either direction)",
								 "           . and ~   any move",
								 "All whitespace and parenthesized text is simply ignored (parentheses must close).",
								 "Use Ctrl-Break to abort an in-progress solve, and Ctrl-C to exit the solver."
							 })
			WriteLine(text);

		Write("\nHIT ENTER FOR MORE HELP (NEXT: COMMANDS YOU CAN TYPE) OR X TO EXIT HELP: ");
		if (ReadLine()?.ToUpper().FirstOrDefault() is 'X')
			return;

		foreach (var text in new[]
							 {
								 "\nType STYLE then Classic, Extended, or PGN (C, E, or P) to set the steno vocabulary.",
								 "Type COOKS then a number (1+) to change the maximum move-sequence cooks retained.",
								 "Type SHOW then a number to set the number of multiple solutions to show",
								 "     (add BOARDS or MOVES to a SHOW command to see boards/FENs or just game-moves).",
								 "Type LIMIT then a number to cap the positions to examine per mark before quitting.",
								 "Type TASKS then a number to set the concurrent solver tasks to run.",
								 "Type FISCHER and a back-rank of piece-abbreviations to set a Fischer Random start.",
								 "Type START or SETUP then a FEN to set the start position. RESET will reset it.",
#if CHUNK_SIZE_CHANGEABLE
								 "Type CHUNK then a number to set the size of checkpoint chunks (default 1K)",
#endif
								 "Type FILE then a filename to send solve results to that file. Omit the name to clear.",
								 "Type SAVE then a filename to save checkpoint data created using $ in the steno.",
								 "Type LOAD then a filename to load checkpoint data that had been saved to disk.",
								 "Type STATUS to see the current status of the settings you control.",
								 "Type QUIT or EXIT (they are just as good as Ctrl-C) to exit the solver."
							 })
			WriteLine(text);

		Write("\nHIT ENTER FOR MORE HELP (NEXT: DETAILS FOR SETTERS) OR X TO EXIT HELP: ");
		if (ReadLine()?.ToUpper().FirstOrDefault() is 'X')
			return;

		foreach (var text in new[]
							 {
								 "\nEach mark in a steno can be followed by:",
								 "        &        then another steno mark; the move must match all",
								 "                 For example:  +&3&R&x&f must be the move Rxf3+",
								 "   [condition]   The position after the move is made must match",
								 "                 the condition(s).  Recognized conditions are:",
								 "        x          xp     a Black pawn was captured on THIS move",
								 "        X          XRnQq  ALL listed pieces must have been captured",
								 "        =          =RnQq  ALL listed pieces must have been promoted",
								 "        ^          ^6     a White pawn must be on rank 6 or 7",
								 "        v          v4     a Black pawn must be on rank 4 or below",
								 "        -          -f4    the f4 square must be vacant",
								 "    R, n, etc.     Qf     a White Queen must be on the f-file",
								 "Conditions may be strung together within [...] using & and | (or).",
								 "Multiple [...][...] conditions can be given (ALL must hold true).",
								 "For - and the piece-position conditions, any part of the square",
								 "can be omitted (as seen in the \"Qf\" example, above).",
								 "Use L (and l) and D (and d) for the light- and dark-square bishops.",
								 "EXAMPLE:  b&x&P[=Q&-7|-8][v3] means a pawn must be moving and",
								 "          capturing on the b-file, that either the eighth rank is",
								 "          completely empty, OR the seventh rank is empty, but if so,",
								 "          then at some point (up to and including this turn),",
								 "          White must have promoted to a queen. Also, Black must",
								 "          have advanced a pawn to the third or second rank.",
								 "Add $ after any single mark in a steno to set the checkpoint there;",
								 "use $ at the beginning of a steno to begin at the checkpoint.",
								 "Type a chunk number (1+) followed, optionally, by a dash then, if" +
								 "desired, a second, ending chunk number, and then * before a steno" +
								 $"to work only specific chunks of {1000:N0} positions from a checkpoint."
							 })
			WriteLine(text);
	}

	void ReportStatus()
	{
		var lingo = solver.Vocabulary switch
					{
						"C" => nameof (Lingo.Classic),
						"E" => nameof (Lingo.Extended),
						"P" => nameof (Lingo.PGN),
						_   => solver.Vocabulary
					};
		foreach (var text in new[]
							 {
								 "\nCURRENT SETTINGS:",
								 $"STYLE {lingo} (STENO-MARKS VOCABULARY)".ToUpper(),
								 $"LIMIT {solver.MaxPositionsToExamine} (MAX POSITIONS TO EXAMINE PER STENO MARK)",
								 $"COOKS {solver.MaxCooksToKeep:N0} (MAX EXTRA MOVE-SETS LEADING TO A POSITION TO KEEP)",
								 $"SHOW  {solver.MaxSolutionsToList} {(solver.DisplayPositions ? "BOARDS" : "MOVES")} (MAX SOLUTIONS TO LIST, AND HOW DETAILED)",
								 $"TASKS {solver.MaxSolverTasks} (MAX CONCURRENT SOLVERS TO RUN)",
#if CHUNK_SIZE_CHANGEABLE
								 solver.CheckpointChunkSize is "0"
									 ? null
									 : $"CHUNK {solver.CheckpointChunkSize} (USE * TO WORK A CHECKPOINT CHUNK; FIRST CHUNK IS 1)",
#endif
								 solver.OutputFile.Any()
									 ? $"FILE  {solver.OutputFile} (RECEIVES SOLVE RESULTS)"
									 : null,
								 solver.SavedSteno.Any()
									 ? $"SAVED ({solver.SavedPositionsCount:N0} POSITION{(solver.SavedPositionsCount is 1 ? null : 'S')}): {solver.SavedSteno}"
									 : null
							 }.Where(static s => s is not null))
			WriteLine(text);
	}
}

foreach (var steno in args)
	solver.Solve(steno);
