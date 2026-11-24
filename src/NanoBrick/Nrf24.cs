using System;
using System.Device.Gpio;
using System.Device.Spi;

namespace NanoBrick
{
    /// <summary>
    /// Pilote minimal du nRF24L01+ en émission seule, réglé pour imiter la télécommande JM039TX :
    /// 1 Mbit/s, adresse de 5 octets, pas d'acquittement, CRC du nRF24 désactivé (on calcule le nôtre).
    /// </summary>
    internal class Nrf24 : IDisposable
    {
        // Registres et commandes utilisés
        private const byte Config = 0x00, EnAa = 0x01, SetupAw = 0x03, SetupRetr = 0x04, RfCh = 0x05,
                           RfSetup = 0x06, Status = 0x07, TxAddr = 0x10, Dynpd = 0x1C, Feature = 0x1D;
        private const byte WRegister = 0x20, WTxPayload = 0xA0, FlushTx = 0xE1;
        private const byte TxDs = 0x20;  // bit « trame partie » du registre STATUS

        private readonly SpiDevice _spi;
        private readonly GpioPin _ce;
        private readonly byte[] _envoi = new byte[33];
        private readonly byte[] _reception = new byte[33];
        private readonly byte[] _unOctet = new byte[1];

        public int EnvoisReussis { get; private set; }
        public int EnvoisRates { get; private set; }

        public Nrf24(int busSpi, int brocheCsn, GpioPin ce)
        {
            _ce = ce;
            _ce.Write(PinValue.Low);
            var reglages = new SpiConnectionSettings(busSpi, brocheCsn)
            {
                ClockFrequency = 4_000_000,
                Mode = SpiMode.Mode0,
                DataFlow = DataFlow.MsbFirst,
            };
            _spi = SpiDevice.Create(reglages);
        }

        /// <summary>Configure le module. Renvoie false s'il ne répond pas (câblage, alimentation).</summary>
        public bool Demarrer(byte[] adresse, int puissance)
        {
            EcrireRegistre(SetupAw, 0x03);  // adresse de 5 octets
            if (LireRegistre(SetupAw) != 0x03) return false;
            EcrireRegistre(EnAa, 0x00);       // pas d'acquittement
            EcrireRegistre(SetupRetr, 0x00);  // pas de retransmission
            EcrireRegistre(Dynpd, 0x00);
            EcrireRegistre(Feature, 0x00);    // trame statique, sans champ de contrôle ESB
            ChoisirPuissance(puissance);

            // Le registre d'adresse s'écrit à l'envers de l'ordre d'émission
            var reg = new byte[5];
            for (int i = 0; i < 5; i++) reg[i] = adresse[4 - i];
            Transferer((byte)(WRegister | TxAddr), reg, 5);
            _adresse = null;

            EcrireRegistre(Config, 0x02);  // allumé, en émission, CRC du nRF24 désactivé
            return true;
        }

        /// <summary>0 à 3 = -18 / -12 / -6 / 0 dBm. Avec un module PA+LNA, rester à 0 (-18 dBm) :
        /// plus fort, le récepteur sature.</summary>
        public void ChoisirPuissance(int niveau)
        {
            if (niveau < 0) niveau = 0;
            if (niveau > 3) niveau = 3;
            EcrireRegistre(RfSetup, (byte)(0x01 | (niveau << 1)));  // 1 Mbit/s
        }

        /// <summary>
        /// Envoie un paquet tout prêt sur un canal et attend qu'il soit parti.
        /// paquet[0] doit valoir <see cref="CommandeEmission"/>, suivi des octets de la trame.
        /// Optimisé pour nanoFramework (code interprété) : chaque appel natif coûte cher, on en fait le
        /// moins possible (3 écritures SPI, 2 écritures GPIO, quelques lectures d'un octet).
        /// </summary>
        public void EnvoyerPaquet(byte canal, byte[] paquet)
        {
            _ecrireCanal[1] = canal;
            _spi.Write(new SpanByte(_ecrireCanal));
            _spi.Write(new SpanByte(EffacerDrapeaux));  // la FIFO est vide : la trame précédente est partie
            _spi.Write(new SpanByte(paquet));

            _ce.Write(PinValue.High);  // une impulsion (plus de 10 µs, toujours le cas ici) lance l'émission
            _ce.Write(PinValue.Low);

            // Toute commande renvoie STATUS en premier octet : une lecture d'un octet suffit
            for (int i = 0; i < 40; i++)
            {
                if ((_spi.ReadByte() & TxDs) != 0)
                {
                    EnvoisReussis++;
                    return;
                }
            }
            EnvoisRates++;
            Transferer(FlushTx, null, 0);  // on vide la trame restée coincée
        }

        /// <summary>
        /// Change l'adresse d'émission (registre TX_ADDR, 5 octets dans l'ordre du registre) pour passer
        /// d'un protocole à l'autre. Ne réécrit rien si c'est déjà ce tableau-là.
        /// Passer des tableaux écrits en dur : sous nanoFramework, un champ statique calculé à partir du
        /// champ statique d'une autre classe peut rester null (vécu : JM039 cassé après le 1er ordre boîtier).
        /// </summary>
        public void ChoisirAdresse(byte[] registre)
        {
            // (un tableau null écrirait FF FF FF FF FF : plus rien ne passerait)
            if (registre == null || registre == _adresse) return;
            Transferer((byte)(WRegister | TxAddr), registre, 5);
            _adresse = registre;
        }

        private byte[] _adresse;

        /// <summary>Premier octet d'un paquet à émettre (commande W_TX_PAYLOAD du nRF24).</summary>
        public const byte CommandeEmission = WTxPayload;

        private readonly byte[] _ecrireCanal = { WRegister | RfCh, 0 };
        private static readonly byte[] EffacerDrapeaux = { WRegister | Status, 0x70 };

        public byte LireRegistre(byte registre)
        {
            Transferer(registre, null, 1);
            return _reception[1];
        }

        private void EcrireRegistre(byte registre, byte valeur)
        {
            _unOctet[0] = valeur;
            Transferer((byte)(WRegister | registre), _unOctet, 1);
        }

        // Envoie la commande puis n octets (0xFF si pas de données) ; la réponse est dans _reception[1..n].
        private void Transferer(byte commande, byte[] donnees, int n)
        {
            _envoi[0] = commande;
            for (int i = 0; i < n; i++) _envoi[i + 1] = donnees == null ? (byte)0xFF : donnees[i];
            _spi.TransferFullDuplex(new SpanByte(_envoi, 0, n + 1), new SpanByte(_reception, 0, n + 1));
        }

        public void Dispose()
        {
            _spi.Dispose();
        }
    }
}
