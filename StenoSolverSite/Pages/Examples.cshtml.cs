using Microsoft.AspNetCore.Mvc.RazorPages;

namespace StenoSolverSite.Pages;

public class ExamplesModel(ILogger<ExamplesModel> logger) : PageModel
{
	private readonly ILogger<ExamplesModel> _logger = logger;

	public void OnGet() { }
}
