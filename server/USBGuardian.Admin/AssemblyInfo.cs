// Zpřístupní internal typy/metody testovacímu projektu (např. HealthService.EvaluateSigningKey) -
// bez toho, aby se pro test musely měnit na public a stát se tím součástí veřejného API.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("USBGuardian.Admin.Tests")]
