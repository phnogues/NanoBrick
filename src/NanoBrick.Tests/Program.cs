// Vérifie que Jm039.Construire produit exactement les trames capturées au renifleur sur la vraie
// télécommande JM039TX (données + CRC). Lancer : dotnet run --project NanoBrick.Tests
using System;
using NanoBrick;

int echecs = 0;

void Verifier(string nom, int commandes, int position, bool bascule, string attendu)
{
    var trame = new byte[Jm039.TailleTrame];
    Jm039.Construire(commandes, position, bascule, trame);
    string obtenu = Convert.ToHexString(trame);
    bool ok = obtenu == attendu;
    if (!ok) echecs++;
    Console.WriteLine($"{(ok ? "ok    " : "ECHEC ")} {nom,-28} {obtenu}{(ok ? "" : "  attendu " + attendu)}");
}

// Trames relevées au renifleur (2026-11-24)
Verifier("pos 1, repos", 0, 1, false, "34CA4292D67E6AED29F91A3F06");
Verifier("pos 1, repos, bascule", 0, 1, true, "34CA4692D67E6AED29F91AA3E9");
Verifier("pos 1, gauche sens 1", Jm039.GaucheSens1, 1, false, "34CA4292D67E6AED29799AB516");
Verifier("pos 1, gauche sens 2", Jm039.GaucheSens2, 1, false, "34CA4292D67E6AED29B95A7A0E");
Verifier("pos 1, droite sens 1", Jm039.DroiteSens1, 1, true, "34CA4692D67E6AED29D97AC9A9");
Verifier("pos 1, droite sens 2", Jm039.DroiteSens2, 1, false, "34CA4292D67E6AED29E90A2E44");
Verifier("pos 1, G1 + D1", Jm039.GaucheSens1 | Jm039.DroiteSens1, 1, true, "34CA4692D67E6AED2959FA43B9");
Verifier("pos 1, G2 + D2", Jm039.GaucheSens2 | Jm039.DroiteSens2, 1, false, "34CA4292D67E6AED29A94A6B4C");
Verifier("pos 1, stop gauche", Jm039.StopGauche, 1, false, "34CA4292D67E6AE929F916347B");
Verifier("pos 1, stop droite", Jm039.StopDroite, 1, false, "34CA4292D67E6AE529F9123BCD");
Verifier("pos 2, repos", 0, 2, false, "34CA4292D67E6AED28F9193855");
Verifier("pos 2, gauche sens 1", Jm039.GaucheSens1, 2, false, "34CA4292D67E6AED287999B245");
Verifier("pos 3, repos", 0, 3, false, "34CA4292D67E6AED2BF9187124");
Verifier("pos 3, gauche sens 2", Jm039.GaucheSens2, 3, false, "34CA4292D67E6AED2BB958342C");
Verifier("pos 4, repos", 0, 4, false, "34CA4292D67E6AED2EF91BAAB7");
Verifier("pos 4, gauche sens 1", Jm039.GaucheSens1, 4, false, "34CA4292D67E6AED2E799B20A7");
Verifier("pos 4, gauche sens 2", Jm039.GaucheSens2, 4, false, "34CA4292D67E6AED2EB95BEFBF");

// Boîtier 4 sorties (télécommande blanche) : trames XN297 brutes captées au renifleur (2026-11-24)
void VerifierBoitier(string nom, int directions, int carres, string attendu)
{
    var trame = new byte[Boitier.TailleTrame];
    Boitier.Construire(directions, carres, trame);
    string obtenu = Convert.ToHexString(trame);
    bool ok = obtenu == attendu;
    if (!ok) echecs++;
    Console.WriteLine($"{(ok ? "ok    " : "ECHEC ")} {nom,-28} {obtenu}{(ok ? "" : "  attendu " + attendu)}");
}

VerifierBoitier("boitier A+", Boitier.APlus, 0, "2675882844BCE5660D2E0C885269F65846");
VerifierBoitier("boitier A-", Boitier.AMoins, 0, "2675882844BCE5660D2E0C8892690691AE");
VerifierBoitier("boitier B+", Boitier.BPlus, 0, "2675882844BCE5660D2E0C88166982A738");
VerifierBoitier("boitier B-", Boitier.BMoins, 0, "2675882844BCE5660D2E0C881A698A5351");
VerifierBoitier("boitier C+", Boitier.CPlus, 0, "2675882844BCE5660D2E0C883269B68BE9");
VerifierBoitier("boitier C-", Boitier.CMoins, 0, "2675882844BCE5660D2E0C880269966A2E");
VerifierBoitier("boitier D+", Boitier.DPlus, 0, "2675882844BCE5660D2E0C881369871C6D");
VerifierBoitier("boitier D-", Boitier.DMoins, 0, "2675882844BCE5660D2E0C88106984755E");
VerifierBoitier("boitier carre gauche", 0, Boitier.CarreGauche, "2675882844BCE5660D2E0C8812E906B16C");
VerifierBoitier("boitier carre droit", 0, Boitier.CarreDroit, "2675882844BCE5660D2E0C081229760497");

Console.WriteLine($"canaux pos 4 : {string.Join(", ", Jm039.CanauxDe(4))}");
Console.WriteLine(echecs == 0 ? "TOUS LES TESTS OK" : $"{echecs} ECHEC(S)");
return echecs;
