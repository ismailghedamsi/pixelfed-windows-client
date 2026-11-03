namespace PixelfedClient.Services;

public enum FediverseAccountKind
{
    Pixelfed,
    Mastodon,
}

public static class FediverseAccountClassifier
{
    private static readonly HashSet<string> PixelfedInstances = new(StringComparer.OrdinalIgnoreCase)
    {
        "pixelfed.social", "gram.social", "pixelfed.uno", "pixelfed.tokyo", "pixelfed.ru",
        "pixelshot.it", "pixelfed.art", "metapixl.com", "pixelfed.au", "fotolibre.social",
        "pixelfed.dk", "pixelfed.com", "pixelfed.com.br", "photofed.world", "milpamerica.org",
        "spicy.social", "pi.dead.guru", "pixelfed.es", "pix.anduin.net", "pixelfed.cz",
        "pellicule.bim.land", "kinkpic.site", "pixelfed.ca", "bolha.photos", "pixelfed.bachgau.social",
        "thistleandmoss.org", "pixelfed.pix-n-chill.fr", "pixl.fi", "pixelfed.global", "pixelfed.eus",
        "instapix.org", "pxlfd.ca", "photos.social", "pics.gayfr.online", "pixelfed.babb.no",
        "pixelfed.ch", "sajin.life", "giftme.love", "pixels.gsi.li", "punk.photos", "pixelfed.ie",
        "gr8.pics", "bonn.pics", "pixelfed.furryfandom.me", "polylux.xyz", "pixel.sp-codes.de",
        "pixelfed.katholisch.social", "pet.tax", "pixelfed.one", "pix.famichiki.jp", "crafty.social",
        "pxlfed.de", "foto.libre.bzh", "chueok.pics", "fischmarkt.social", "is.this.photography",
        "paw.photo", "pixaroid.de", "pixelfed.apobangpo.art", "photos.grossermensch.eu",
        "pixelfed.mesnumeriques.fr", "biketravel.photos", "idar.us", "pixelfed.li",
    };

    public static FediverseAccountKind Classify(string? acct, string? profileUrl, string? localInstanceHost)
    {
        var host = GetHost(profileUrl);
        if (host is null && !string.IsNullOrWhiteSpace(acct))
        {
            var separator = acct.LastIndexOf('@');
            if (separator >= 0 && separator < acct.Length - 1)
            {
                host = NormalizeHost(acct[(separator + 1)..]);
            }
        }

        host ??= NormalizeHost(localInstanceHost);
        return host is not null && PixelfedInstances.Contains(host)
            ? FediverseAccountKind.Pixelfed
            : FediverseAccountKind.Mastodon;
    }

    private static string? GetHost(string? profileUrl) =>
        Uri.TryCreate(profileUrl, UriKind.Absolute, out var uri) ? NormalizeHost(uri.Host) : null;

    private static string? NormalizeHost(string? host)
    {
        var normalized = host?.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized is null)
        {
            return null;
        }

        return normalized.StartsWith("www.", StringComparison.Ordinal) ? normalized[4..] : normalized;
    }
}
