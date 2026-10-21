using System.Windows.Markup;
using Constantino.Core;

namespace Constantino.App.Controles;

/// <summary>
/// Un texto fijo de la interfaz en el idioma elegido: <c>Text="{l:T 'Guardar'}"</c>. El texto en español es la
/// clave de la traducción (ver <see cref="Idioma"/>).
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string texto) : MarkupExtension
{
    public string Texto { get; } = texto;

    public override object ProvideValue(IServiceProvider serviceProvider) => Idioma.T(Texto);
}
