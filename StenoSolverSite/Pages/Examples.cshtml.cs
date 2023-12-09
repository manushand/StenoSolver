using Microsoft.AspNetCore.Mvc.RazorPages;

namespace StenoSolverSite.Pages;

public class ExamplesModel : PageModel
{
	private readonly ILogger<ExamplesModel> _logger;

	public ExamplesModel(ILogger<ExamplesModel> logger)
		=> _logger = logger;

	public void OnGet() { }
}
