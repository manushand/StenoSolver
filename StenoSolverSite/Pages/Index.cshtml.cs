using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using steno;
using static steno.Solver;
using static steno.Solver.Lingo;

namespace StenoSolverSite.Pages;

public class IndexModel(ILogger<IndexModel> logger) : PageModel
{
	protected internal const string Checkpoint = nameof (Checkpoint);

	[BindProperty]
	public string Input { get; set; } = string.Empty;

	[BindProperty]
	public string Vocabulary { get; set; } = $"{Extended}";

	[BindProperty]
	public string MaxPositionsToExamine { get; set; } = "200K";

	[BindProperty]
	public string MaxSolutionsToList { get; set; } = "2";

	[BindProperty]
	public string DisplayPositions { get; set; } = "false";

	private readonly ILogger<IndexModel> _logger = logger;

	public void OnGet()
	{
		//  Set initial defaults
		var solver = new Solver(UpdatePage);
		MaxPositionsToExamine = $"{solver.MaxPositionsToExamine}";
		MaxSolutionsToList = $"{solver.MaxSolutionsToList}";
		DisplayPositions = $"{solver.DisplayPositions}";
	}

	public void OnPost()
	{
		var session = HttpContext.Session;
		var maxSolutionsToList = int.Parse(MaxSolutionsToList);
		var displayPositions = DisplayPositions is not "none" && bool.Parse(DisplayPositions);
		var solver = new Solver(UpdatePage,
								Vocabulary,
								MaxPositionsToExamine,
								maxCooksToKeep: 2,
								maxSolutionsToList: maxSolutionsToList,
								displayPositions: displayPositions,
								allowChunking: false); // checkpoint chunking is disabled on the Website
		SetCheckpointFromSession();
		solver.Solve(Input);
		SaveCheckpointInSession();

		void SetCheckpointFromSession()
		{
			if (session.Keys.Contains(Checkpoint))
				solver.Checkpoint = session.Get(Checkpoint) ?? throw new ();
		}

		void SaveCheckpointInSession()
		{
			session.Set(Checkpoint, solver.Checkpoint);
			ViewData[Checkpoint] = solver.SavedSteno;
		}
	}

	private void UpdatePage(string text, MessageType type)
	{
		//	For now, just discard all "work in progress" reports,
		//	because we don't update the webpage in realtime yet.
		if (type is not MessageType.InProgress)
			ViewData[$"{type}"] += $"{text}\n";
	}
}
