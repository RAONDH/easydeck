#!/bin/zsh
# 로그인 키체인에 'EasyDeck Local Signing' 자체 서명 인증서를 만듭니다 (이 Mac에서만 사용).
# 한 번 만들어 두면 build.sh 가 이 인증서로 서명해서, 다시 빌드해도 손쉬운 사용 권한이 유지됩니다.
set -e
NAME="EasyDeck Local Signing"
if security find-identity -p codesigning | grep -q "\"$NAME\""; then
  echo "이미 있습니다: $NAME"; exit 0
fi
W=$(mktemp -d); trap 'rm -rf "$W"' EXIT
cat > "$W/cert.cnf" <<CNF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = $NAME
[ext]
basicConstraints = critical,CA:false
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
CNF
/usr/bin/openssl req -x509 -newkey rsa:2048 -nodes -keyout "$W/key.pem" -out "$W/cert.pem" -days 3650 -config "$W/cert.cnf" 2>/dev/null
PASS=$(/usr/bin/openssl rand -hex 12)
/usr/bin/openssl pkcs12 -export -inkey "$W/key.pem" -in "$W/cert.pem" -name "$NAME" -out "$W/id.p12" -passout pass:$PASS
security import "$W/id.p12" -k ~/Library/Keychains/login.keychain-db -P "$PASS" -T /usr/bin/codesign
echo "✅ 만들었습니다: $NAME"
