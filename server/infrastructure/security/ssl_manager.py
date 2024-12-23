# infrastructure/security/ssl_manager.py
import ssl
import logging
from typing import Optional
from pathlib import Path
from cryptography import x509
from cryptography.x509.oid import NameOID
from cryptography.hazmat.primitives import serialization, hashes
from cryptography.hazmat.primitives.asymmetric import rsa
import datetime
from infrastructure.logging.custom_logger import get_logger

class SSLManager:
    """
    SSLManager ensures that we have valid SSL certificates and keys for the server.
    If none exist, it generates a self-signed certificate and private key.
    """

    def __init__(self, cert_file: str, key_file: str, logger: Optional[logging.Logger] = None):
        self.cert_file = cert_file
        self.key_file = key_file
        self.logger = logger or get_logger("SSLManager", debug_mode=False)
        self.logger.debug(f"SSLManager initialized with cert_file='{self.cert_file}', key_file='{self.key_file}'")

    def get_ssl_context(self) -> Optional[ssl.SSLContext]:
        cert_path = Path(self.cert_file)
        key_path = Path(self.key_file)

        # Ensure directories exist
        if not cert_path.parent.exists():
            cert_path.parent.mkdir(parents=True, exist_ok=True)
            self.logger.debug(f"Created directory for certificates at '{cert_path.parent}'")
        if not key_path.parent.exists():
            key_path.parent.mkdir(parents=True, exist_ok=True)
            self.logger.debug(f"Created directory for keys at '{key_path.parent}'")

        if not cert_path.exists() or not key_path.exists():
            self.logger.warning("SSL certificates not found. Generating a self-signed certificate...")

            # Generate a new RSA key
            self.logger.debug("Generating RSA private key.")
            key = rsa.generate_private_key(
                public_exponent=65537,
                key_size=2048,
            )

            self.logger.debug("Building a self-signed certificate.")
            subject = issuer = x509.Name([
                x509.NameAttribute(NameOID.COUNTRY_NAME, "US"),
                x509.NameAttribute(NameOID.STATE_OR_PROVINCE_NAME, "CA"),
                x509.NameAttribute(NameOID.LOCALITY_NAME, "Locality"),
                x509.NameAttribute(NameOID.ORGANIZATION_NAME, "MyOrg"),
                x509.NameAttribute(NameOID.COMMON_NAME, "localhost"),
            ])

            cert = (
                x509.CertificateBuilder()
                .subject_name(subject)
                .issuer_name(issuer)
                .public_key(key.public_key())
                .serial_number(x509.random_serial_number())
                .not_valid_before(datetime.datetime.utcnow())
                .not_valid_after(
                    datetime.datetime.utcnow() + datetime.timedelta(days=365)
                )
                .add_extension(
                    x509.SubjectAlternativeName([x509.DNSName("localhost")]),
                    critical=False,
                )
                .sign(key, hashes.SHA256())
            )

            self.logger.debug("Writing private key and certificate to disk.")
            # Write the private key
            with open(self.key_file, "wb") as f:
                f.write(
                    key.private_bytes(
                        encoding=serialization.Encoding.PEM,
                        format=serialization.PrivateFormat.TraditionalOpenSSL,
                        encryption_algorithm=serialization.NoEncryption()
                    )
                )

            # Write the certificate
            with open(self.cert_file, "wb") as f:
                f.write(cert.public_bytes(serialization.Encoding.PEM))

            self.logger.info("Self-signed SSL certificate and key generated successfully.")

        # Now load the cert and key
        try:
            ctx = ssl.create_default_context(ssl.Purpose.CLIENT_AUTH)
            ctx.load_cert_chain(certfile=self.cert_file, keyfile=self.key_file)
            self.logger.info("SSL enabled with provided (or self-signed) certificate and key.")
            return ctx
        except Exception as e:
            self.logger.exception(f"Failed to create SSL context: {e}")
            return None
