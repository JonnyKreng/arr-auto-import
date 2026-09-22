FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY main.py .
COPY engines/ ./engines/

EXPOSE 8000

# DECISION_ENGINE selects the backend: 'llm' (hosted OpenAI-compatible chat, no
# checkpoint) or 'laya' (downloads its checkpoints on first boot, warm-up in
# lifespan): laya typed-decisions ~0.5 GB.
VOLUME ["/root/.cache/huggingface"]

# First boot is slow while checkpoints download; be generous with start-period.
HEALTHCHECK --interval=30s --timeout=5s --start-period=600s \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://localhost:8000/health')"

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]