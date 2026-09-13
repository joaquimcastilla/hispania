using Hispania.Media.Domain.Assets;

namespace Hispania.Media.Domain.TechnicalRepresentations;

public sealed record TextTechnicalProfile : TechnicalProfile
{
    public TextTechnicalProfile(FormatIdentifier format, CharacterEncoding? characterEncoding = null)
    {
        Format = format ?? throw new DomainValidationException("text-technical-profile.format.required", "A text format is required.");
        CharacterEncoding = characterEncoding;
    }

    public override AssetMediaKind MediaKind => AssetMediaKind.Text;

    public FormatIdentifier Format { get; }

    public CharacterEncoding? CharacterEncoding { get; }
}
