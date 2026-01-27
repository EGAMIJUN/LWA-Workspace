# 🤖 Legacy Wrapper Agent (LWA)
### Turning "Legacy Desktop Apps" into "Modern AI Agents" 🚀

<div align="center">

![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows-0078D6?style=for-the-badge&logo=windows&logoColor=white)
![AI](https://img.shields.io/badge/AI-AWS%20Bedrock-232F3E?style=for-the-badge&logo=amazon-aws&logoColor=white)
![License](https://img.shields.io/badge/License-MIT-green?style=for-the-badge)

**Bridging the gap between 20-year-old Legacy Systems and Cutting-Edge AI.** *Automate the Unautomatable.*

[Demo Video (Coming Soon)] | [Documentation](#) | [Report Bug](../../issues)

</div>

---

## 📖 Overview

**LWA (Legacy Wrapper Agent)** is a specialized automation layer designed for **Port Logistics & Enterprise Legacy Systems**.

Many critical industries (Logistics, Manufacturing, Healthcare) still rely on legacy **VB.NET / WinForms / MFC** applications that lack modern APIs. Rewriting them is costly and risky.

**LWA solves this by wrapping the legacy UI with an AI-driven control layer**, enabling:
- **Zero-Code Integration:** Control desktop apps without modifying their source code.
- **AI-Native Interface:** Interact with legacy apps using Natural Language (via AWS Bedrock / OpenAI).
- **Autonomous Recovery:** AI detects UI errors and retries operations automatically.

---

## 🏗 Architecture

LWA does not just "click buttons". It understands the UI structure.

```mermaid
graph TD
    User[User / External System] -->|REST API / MQTT| Agent[🤖 LWA Core Agent]
    
    subgraph "Legacy Environment (Windows)"
        Agent -->|UI Automation / Computer Vision| LegacyApp[🖥 Legacy Desktop App]
        LegacyApp -->|Screen State / Logs| Agent
    end
    
    subgraph "Cloud Brain (Optional)"
        Agent <-->|Context & Reasoning| LLM[🧠 AWS Bedrock / GPT-4o]
    end
