// Zpřístupní internal typy/metody testovacímu projektu (např. DeviceBlocker.InterpretBlockOutput) -
// bez toho, aby se pro test musely měnit na public a stát se tím součástí veřejného API.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("USBGuardian.Agent.Tests")]
