import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
export default defineConfig({base:'/app/mimotts/',plugins:[vue()],server:{proxy:{'/app/mimotts/api':'http://127.0.0.1:5080'}}})
