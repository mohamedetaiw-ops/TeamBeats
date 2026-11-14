# Apache Tika Server (Apache-2.0) with Tesseract OCR for document text (spec 011 FR-020). The official full image ships
# English and other European language packs; Arabic is added here because TeamBeats launches in Arabic and English.
FROM apache/tika:3.2.3.0-full
USER root
RUN apt-get update \
 && apt-get install -y --no-install-recommends tesseract-ocr-ara \
 && rm -rf /var/lib/apt/lists/*
USER 35002:35002
