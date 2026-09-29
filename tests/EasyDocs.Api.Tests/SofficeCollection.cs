// Every test class that starts soffice. xUnit runs a DisableParallelization collection on its own,
// after the parallel ones, so no two LibreOffice processes (or their first-start profile setup) ever
// race each other — a conversion that CI once saw exit with no output while the PDF tests also ran.
[CollectionDefinition(Name, DisableParallelization = true)]
public class SofficeCollection
{
    public const string Name = "soffice";
}
