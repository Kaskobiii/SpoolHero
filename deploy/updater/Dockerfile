FROM docker:28-cli
RUN apk add --no-cache python3 bash gzip
WORKDIR /updater
COPY deploy/updater/server.py /updater/server.py
EXPOSE 8091
ENTRYPOINT ["python3", "/updater/server.py"]
