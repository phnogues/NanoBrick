// Protocole du boîtier batterie 4 sorties (télécommande blanche) : puce XN297 imitée sur le nRF24.
// Le codage XN297 (brouillage + CRC) est porté de ../../tools/JM039TX/xn297.h, lui-même tiré de
// DIY-Multiprotocol-TX-Module (GPLv3) : ce fichier est donc sous GPLv3.

namespace NanoBrick
{
    /// <summary>
    /// Trames du boîtier 4 sorties. Tout ou rien : la sortie tourne à pleine vitesse tant qu'on « appuie »
    /// (une trame toutes les 8 ms), s'arrête à la première trame « rien d'appuyé ». Pas de vitesse.
    /// Le boîtier s'appaire à la première télécommande qu'il entend : aucun identifiant dans la trame.
    /// </summary>
    public static class Boitier
    {
        public const byte Canal = 2;
        public const int TramesDeFin = 20;     // trames « rien d'appuyé » après un relâchement
        public const int TailleTrame = 17;     // 5 d'adresse brouillée + 10 de données + 2 de CRC

        /// <summary>Adresse XN297 (ordre de la télécommande).</summary>
        public static readonly byte[] Adresse = { 0xC1, 0xC2, 0xC3, 0xC4, 0xC5 };

        /// <summary>Registre TX_ADDR du nRF24 (ordre du registre) : préambule XN297 71 0F 55 + 0C 00.</summary>
        public static readonly byte[] RegistreAdresse = { 0x55, 0x0F, 0x71, 0x0C, 0x00 };

        // Boutons de direction (octet 7) : sortie A haut/bas, B droite/gauche, C haut/bas, D droite/gauche
        public const int APlus = 0x02, AMoins = 0x01;
        public const int BPlus = 0x20, BMoins = 0x10;
        public const int CPlus = 0x04, CMoins = 0x08;
        public const int DPlus = 0x80, DMoins = 0x40;

        // Boutons carrés (octet 8)
        public const int CarreGauche = 0x01, CarreDroit = 0x02;

        /// <summary>Bouton « + » ou « − » d'une sortie (0 = A … 3 = D).</summary>
        public static int Bouton(int sortie, bool plus)
        {
            switch (sortie)
            {
                case 0: return plus ? APlus : AMoins;
                case 1: return plus ? BPlus : BMoins;
                case 2: return plus ? CPlus : CMoins;
                default: return plus ? DPlus : DMoins;
            }
        }

        /// <summary>
        /// Construit la trame à pousser dans le nRF24 (CRC du nRF désactivé) :
        /// données 00 00 00 00 01 01 S6 DIR SQ SOMME, puis codage XN297.
        /// </summary>
        public static void Construire(int directions, int carres, byte[] trame)
        {
            var m = new byte[10];
            m[4] = 0x01;
            m[5] = 0x01;
            m[6] = (byte)((carres & CarreDroit) != 0 ? 0x01 : 0x00);
            m[7] = (byte)directions;
            m[8] = (byte)carres;
            int somme = 0x14;
            for (int i = 0; i < 9; i++) somme += m[i];
            m[9] = (byte)somme;

            uint lfsr = 0xE3B14BE0;   // blanchiment XN297 (graine 0x71)
            ushort crc = 0xFFFF;
            int n = 0;
            for (int i = 0; i < 5; i++) trame[n++] = Brouiller(Adresse[4 - i], ref lfsr, ref crc);
            for (int i = 0; i < 10; i++) trame[n++] = Brouiller(Inverser(m[i]), ref lfsr, ref crc);
            ushort fin = (ushort)(~crc ^ (lfsr >> 16));
            trame[n++] = (byte)(fin >> 8);
            trame[n] = (byte)fin;
        }

        // CRC16-CCITT sur l'octet en clair, puis XOR avec le blanchiment
        private static byte Brouiller(byte b, ref uint lfsr, ref ushort crc)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            byte s = (byte)(lfsr >> 24);
            lfsr = (lfsr << 8) | ((uint)(byte)(s ^ (lfsr >> 8)) << 4);
            return (byte)(b ^ s);
        }

        // La XN297 émet les données bit de poids faible en premier
        private static byte Inverser(byte b)
        {
            int r = 0;
            for (int i = 0; i < 8; i++)
            {
                r = (r << 1) | (b & 1);
                b >>= 1;
            }
            return (byte)r;
        }
    }
}
