namespace Vessel.Domain.Certificates;

public enum CertificateProvider
{
    TraefikAcme,
    LetsEncrypt,
    Custom
}
