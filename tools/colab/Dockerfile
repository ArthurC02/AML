FROM python:3.12-slim
RUN apt-get update && apt-get install -y --no-install-recommends openssh-client ca-certificates && rm -rf /var/lib/apt/lists/* \
 && pip install --no-cache-dir google-colab-cli keyrings.alt
ENV PYTHON_KEYRING_BACKEND=keyrings.alt.file.PlaintextKeyring HOME=/home/colab
WORKDIR /work
ENTRYPOINT ["colab"]
